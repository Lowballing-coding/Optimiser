using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Optimiser;

public partial class ShotTrainerPage : UserControl
{
    bool loading = true, sample; // loading until the settings are shown, so setting up the sliders saves nothing

    public ShotTrainerPage()
    {
        InitializeComponent();
        ShowShots(Training.BuiltInShots);
        for (var level = 1; level < Training.Levels.Length; level++)
        {
            var pick = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "Level", Content = Training.Levels[level], Tag = level };
            pick.Checked += OnLevel;
            LevelPicker.Children.Add(pick);
        }
        ticker.Tick += (_, _) => OnTick();
        saver.Tick += (_, _) => SaveNow();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) LetGo();
            else if (!sample) Show();
        };
    }

    // One switch per shot. The list follows the trainer's (custom shots included), so it's rebuilt when that changes.
    void ShowShots(string[] names)
    {
        if (ShotList.Children.Cast<DockPanel>().Select(r => ((TextBlock)r.Children[1]).Text).SequenceEqual(names)) return;
        ShotList.Children.Clear();
        for (var i = 0; i < names.Length; i++)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, i % 2 == 0 ? 40 : 0, 12) };
            var box = new CheckBox { Style = (Style)FindResource("Switch"), Tag = i };
            box.Click += OnShot;
            AutomationProperties.SetName(box, names[i]);
            DockPanel.SetDock(box, Dock.Right);
            row.Children.Add(box);
            row.Children.Add(new TextBlock { Text = names[i], VerticalAlignment = VerticalAlignment.Center });
            ShotList.Children.Add(row);
        }
    }

    // Re-read each time the page opens: the trainer's own window may have changed the file.
    void Show()
    {
        if (!Training.Installed)
        {
            (State.Text, StateDot.Fill) = ($"The shot trainer isn't in {Training.Folder}.", Paint("Muted"));
            Buttons.Visibility = Keys.Visibility = WithGameRow.Visibility = WithGamePanel.Visibility = Controls.Visibility = Visibility.Collapsed;
            return;
        }
        Buttons.Visibility = Keys.Visibility = WithGameRow.Visibility = WithGamePanel.Visibility = Controls.Visibility = Visibility.Visible;
        (State.Text, StateDot.Fill) = Training.Running ? ("Training is running.", Paint("Good"))
                                    : Training.GameUpdating() ? ($"{Training.UpdatingNote} Training can start once it's done.", Paint("Warn"))
                                    : ("Not running. Start it here or from the RL Trainer shortcut.", Paint("Muted"));
        WithGame.IsChecked = Settings.Get(Training.WithGameSetting, false);
        WithShift.IsChecked = Settings.Get(Training.ShiftSetting, false);
        WithShift.IsEnabled = Training.ShiftInstalled;
        if (!Training.ShiftInstalled) ShiftNote.Text = $"Shift isn't installed in {Path.GetDirectoryName(Training.ShiftExe)}.";
        ShowShots(Training.Shots);
        ShowSettings(Training.Load());
        if (ClipsTab.IsVisible) ShowClips();
        if (StatsTab.IsVisible) ShowStats();
    }

    // The fallbacks are the trainer's own defaults (DEFAULTS in sliders.py).
    void ShowSettings(JsonObject s)
    {
        loading = true;
        Skill.Value = Training.Number(s, "bot_skill", 70);
        HeadStart.Value = Training.Number(s, "head_start", 0.5);
        Countdown.Value = Training.Number(s, "countdown", 3);
        BallSpeed.Value = Training.Number(s, "ball_speed", 100);
        ShotTime.Value = Training.Number(s, "shot_time", 100);
        Variation.Value = Training.Number(s, "variation", 0);
        RandomOrder.IsChecked = s["order"]?.ToString() == "Random";
        Repeat.IsChecked = Training.Flag(s, "repeat", false);
        ShowPath.IsChecked = Training.Flag(s, "show_path", true);
        Replay.IsChecked = Training.Flag(s, "replay", true);
        ReplayAll.IsChecked = Training.Flag(s, "replay_all", true);
        InfiniteBoost.IsChecked = Training.Flag(s, "infinite_boost", false);
        AutoDifficulty.IsChecked = Training.Flag(s, "auto_difficulty", false);
        Adaptive.IsChecked = Training.Flag(s, "adaptive", false);
        var level = (int)Training.Number(s, "player_level", 2); // the trainer moves it while adapting
        foreach (RadioButton pick in LevelPicker.Children) pick.IsChecked = (int)pick.Tag == level;
        var shots = s["shots"] as JsonArray;
        foreach (DockPanel row in ShotList.Children)
        {
            var box = (CheckBox)row.Children[0];
            var i = (int)box.Tag;
            box.IsChecked = shots == null || i >= shots.Count || (shots[i] is JsonValue v && v.TryGetValue(out bool on) && on);
        }
        loading = false;
    }

    void Change(Action<JsonObject> change)
    {
        if (loading || sample) return;
        var s = Training.Load();
        change(s);
        try { Training.Save(s); }
        catch (Exception e) { (State.Text, StateDot.Fill) = ($"Couldn't save the settings: {e.Message}", Paint("Warn")); }
    }

    void OnSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var slider = (Slider)sender;
        Change(s => s[(string)slider.Tag] = Math.Round(slider.Value, 1));
    }

    void OnWithGame(object sender, RoutedEventArgs e) => Settings.Set(Training.WithGameSetting, WithGame.IsChecked == true);

    void OnWithShift(object sender, RoutedEventArgs e) => Settings.Set(Training.ShiftSetting, WithShift.IsChecked == true);

    void OnSwitch(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        var key = (string)box.Tag;
        Change(s => s[key] = key == "order" ? (box.IsChecked == true ? "Random" : "In order") : (JsonNode)(box.IsChecked == true));
    }

    void OnShot(object sender, RoutedEventArgs e) => Change(s =>
        s["shots"] = new JsonArray(ShotList.Children.Cast<DockPanel>().Select(r => (JsonNode?)(((CheckBox)r.Children[0]).IsChecked == true)).ToArray()));

    void OnLaunch(object sender, RoutedEventArgs e)
    {
        Training.LaunchGame();
        if (Training.GameUpdating()) (State.Text, StateDot.Fill) = ($"{Training.UpdatingNote} It opens when the update is done.", Paint("Warn"));
    }

    void OnMatch(object sender, RoutedEventArgs e)
    {
        if (Training.Running)
        {
            (State.Text, StateDot.Fill) = ("Training is running. Close Rocket League and the trainer window first.", Paint("Warn"));
            return;
        }
        if (!Training.Match())
        {
            (State.Text, StateDot.Fill) = ("Match mode isn't set up in the trainer folder yet.", Paint("Warn"));
            return;
        }
        (State.Text, StateDot.Fill) = Training.GameUpdating()
            ? ($"{Training.UpdatingNote} The match starts by itself when it's done.", Paint("Warn"))
            : ("Starting a 1v1 match against Necto.", Paint("Warn"));
    }

    void OnStart(object sender, RoutedEventArgs e)
    {
        if (Training.Running)
        {
            (State.Text, StateDot.Fill) = ("Training is already running.", Paint("Good"));
            return;
        }
        Training.Start();
        (State.Text, StateDot.Fill) = Training.GameUpdating()
            ? ($"{Training.UpdatingNote} Training starts by itself when it's done.", Paint("Warn"))
            : ("Starting Rocket League with the trainer. The first shot comes after the kickoff.", Paint("Warn"));
    }

    void OnLeave(object sender, RoutedEventArgs e) =>
        (State.Text, StateDot.Fill) = Training.Leave()
            ? ("Leaving training. Rocket League reopens normally, ready for online play.", Paint("Warn"))
            : ("Leaving training isn't set up in the trainer folder yet.", Paint("Warn"));

    void OnRetry(object sender, RoutedEventArgs e) => Command("retry");

    void OnSave(object sender, RoutedEventArgs e) => Command("save");

    void OnNext(object sender, RoutedEventArgs e) => Command("next");

    void Command(string command)
    {
        if (sample) return;
        try { Training.Send(command); }
        catch (Exception e) { (State.Text, StateDot.Fill) = ($"Couldn't reach the trainer: {e.Message}", Paint("Warn")); }
    }

    void OnFolder(object sender, RoutedEventArgs e) => Tweaks.OpenAsYou(Training.Folder);

    void OnTab(object sender, RoutedEventArgs e)
    {
        if (StatsTab == null) return; // the first tab is checked while the page loads
        var tab = sender == ClipsTabButton ? ClipsTab : sender == StatsTabButton ? StatsTab : sender == LibraryTabButton ? LibraryTab : TrainingTab;
        foreach (var t in new[] { TrainingTab, LibraryTab, ClipsTab, StatsTab }) t.Visibility = t == tab ? Visibility.Visible : Visibility.Collapsed;
        if (tab == ClipsTab) ShowClips();
        else LetGo();
        if (tab == StatsTab && !sample) ShowStats();
        if (tab == LibraryTab && !sample) ShowLibrary();
    }

    void ShowLibrary()
    {
        var shots = Training.Shots;
        ShowLibrary(Training.Library(shots), Training.PerShot(Training.Attempts(), shots));
    }

    // A card per shot, grouped by category in the trainer's order, with Peter's record on each.
    void ShowLibrary(List<ShotInfo> shots, List<ShotStat> stats)
    {
        var level = LevelPicker.Children.Cast<RadioButton>().FirstOrDefault(p => p.IsChecked == true)?.Tag as int? ?? 2;
        LibraryList.Children.Clear();
        foreach (var group in shots.GroupBy(s => s.Category))
        {
            LibraryList.Children.Add(new TextBlock { Text = group.Key, Style = (Style)FindResource("GroupTitle") });
            var cards = new WrapPanel();
            foreach (var shot in group)
            {
                var stat = shot.Index < stats.Count ? stats[shot.Index] : null;
                var body = new StackPanel();
                body.Children.Add(new ShotPicture { Shot = shot, Margin = new Thickness(0, 0, 0, 12) });
                var top = new DockPanel();
                if (shot.Level > 0)
                {
                    var badge = new Border { Style = (Style)FindResource("Badge"), Child = new TextBlock { Text = Training.Levels[shot.Level] } };
                    DockPanel.SetDock(badge, Dock.Right);
                    top.Children.Add(badge);
                }
                top.Children.Add(new TextBlock { Text = shot.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                body.Children.Add(top);
                if (shot.About != "")
                    body.Children.Add(new TextBlock { Text = shot.About, Style = (Style)FindResource("MutedText"), FontSize = 12.5, Margin = new Thickness(0, 6, 0, 0) });
                body.Children.Add(new TextBlock
                {
                    Text = stat?.Info ?? "Not tried yet", FontSize = 12, Margin = new Thickness(0, 10, 0, 0),
                    Foreground = Paint(stat?.Weakest == true ? "Accent" : "Muted"),
                });
                var card = new Button { Style = (Style)FindResource("ShotCard"), Content = body, Tag = shot, MinHeight = 120 };
                if (shot.Level == level) card.BorderBrush = Paint("Accent");
                AutomationProperties.SetName(card, $"Play {shot.Name}");
                card.Click += OnCard;
                cards.Children.Add(card);
            }
            LibraryList.Children.Add(cards);
        }
    }

    void OnLevel(object sender, RoutedEventArgs e)
    {
        Change(s => s["player_level"] = (int)((RadioButton)sender).Tag);
        if (LibraryTab.IsVisible && !sample) ShowLibrary();
    }

    // Plays the shot next. With the trainer closed, only that shot is ticked and training starts.
    void OnCard(object sender, RoutedEventArgs e)
    {
        if (sample || ((Button)sender).Tag is not ShotInfo shot) return;
        LibraryNote.Visibility = Visibility.Visible;
        try
        {
            if (Training.Running)
            {
                Training.Send("play", shot.Index);
                LibraryNote.Text = $"Up next: {shot.Name}.";
                return;
            }
            if (!Training.Installed) { LibraryNote.Text = $"The shot trainer isn't in {Training.Folder}."; return; }
            var count = Training.Shots.Length;
            Change(s => s["shots"] = new JsonArray([.. Enumerable.Range(0, count).Select(i => (JsonNode?)(i == shot.Index))]));
            ShowSettings(Training.Load());
            OnStart(sender, e);
            LibraryNote.Text = $"Starting Rocket League with only {shot.Name} ticked. Tick more on the Training tab.";
        }
        catch (Exception ex) { LibraryNote.Text = $"Couldn't reach the trainer: {ex.Message}"; }
    }

    void ShowStats()
    {
        var all = Training.Attempts();
        ShowStats(all, Training.PerShot(all, Training.Shots), Training.LastSession(all));
    }

    void ShowStats(List<Attempt> all, List<ShotStat> stats, (List<Attempt> Attempts, int Clips)? last)
    {
        (NoStats.Visibility, StatsView.Visibility) = all.Count == 0 ? (Visibility.Visible, Visibility.Collapsed) : (Visibility.Collapsed, Visibility.Visible);
        ShotStats.ItemsSource = stats;
        if (last is not { } session) return;
        var (tries, clips) = session;
        var made = tries.Count(a => a.Ok);
        SessionText.Text = $"{made} of {tries.Count} made   {(double)made / tries.Count:P0}";
        var mine = Training.PerShot(tries, Training.Shots).Where(s => s.Tries > 0).ToList();
        var best = mine.OrderByDescending(s => s.Rate).ThenByDescending(s => s.Tries).FirstOrDefault();
        var worst = mine.OrderBy(s => s.Rate).ThenByDescending(s => s.Tries).FirstOrDefault();
        SessionDetail.Text = $"{tries[0].Time:d MMM, HH:mm}   ·   Best streak {Training.BestStreak(tries)}   ·   {clips} clip{(clips == 1 ? "" : "s")}"
            + (best == null ? "" : $"   ·   Best: {best.Shot} ({best.Made}/{best.Tries})")
            + (worst == null || worst == best ? "" : $"   ·   Toughest: {worst.Shot} ({worst.Made}/{worst.Tries})");
    }

    // Ticks only the shots that need work, and starts the trainer (or the running one picks them up at the next shot).
    void OnTrainWeak(object sender, RoutedEventArgs e)
    {
        if (sample) return;
        var stats = Training.PerShot(Training.Attempts(), Training.Shots);
        var weak = Training.Weakest(stats, 3);
        Change(s => s["shots"] = new JsonArray(stats.Select((_, i) => (JsonNode?)weak.Contains(i)).ToArray()));
        ShowSettings(Training.Load());
        StatsNote.Text = $"Training {string.Join(", ", weak.Select(i => stats[i].Shot))}.";
        if (!Training.Running && Training.Installed) OnStart(sender, e);
    }

    void ShowClips(string? pick = null)
    {
        Pause();
        Player.Source = null;
        var all = Training.Clips();
        var clips = FavouritesOnly.IsChecked == true ? all.Where(c => c.Favourite).ToList() : all;
        ClipList.ItemsSource = clips;
        NoClips.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipView.Visibility = all.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoFavourites.Visibility = all.Count > 0 && clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (clips.Count == 0) { Pick(null); return; }
        var index = Math.Max(0, clips.FindIndex(c => c.File == pick));
        ClipList.UpdateLayout(); // so the row exists to be picked
        if (ClipList.ItemContainerGenerator.ContainerFromIndex(index) is ContentPresenter row
            && VisualTreeHelper.GetChildrenCount(row) > 0 && VisualTreeHelper.GetChild(row, 0) is RadioButton first)
        {
            autoPicked = true; // shows its first frame; a clip Peter clicks plays straight away
            first.IsChecked = true;
            autoPicked = false;
        }
    }

    ShotClip? clip;
    bool playing, seeking, showing; // showing: the edit controls are being set from a clip, so changing them saves nothing
    readonly ColourEffect colour = new();
    readonly System.Windows.Threading.DispatcherTimer ticker = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly System.Windows.Threading.DispatcherTimer saver = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly Stack<ClipEdit> undo = new(), redo = new();
    bool saving, autoPicked;

    void OnClip(object sender, RoutedEventArgs e) => Pick((ShotClip)((FrameworkElement)sender).DataContext);

    // Closes the video when the tab or page is left, so the trainer can re-encode or replace it.
    void LetGo()
    {
        Pause();
        SaveNow();
        Player.Source = null;
    }

    void Pick(ShotClip? next)
    {
        SaveNow();
        clip = next;
        if (clip == null) { (ClipName.Text, ClipInfo.Text) = ("", ""); return; }
        (ClipName.Text, ClipInfo.Text) = (clip.Shot, clip.Info);
        ShowFavourite();
        ShowEdit();
        undo.Clear();
        redo.Clear();
        Player.Source = new Uri(clip.Source);
        Player.Play();
        if (autoPicked) { Pause(); return; } // Pause straight after Play shows the first frame
        (playing, PlayButton.Content) = (true, "\uE769"); // the pause icon
        ticker.Start();
    }

    void ShowFavourite() => (FavouriteButton.Content, FavouriteButton.Foreground) =
        clip?.Favourite == true ? ("\uE735", Paint("Accent")) : ("\uE734", Paint("Text"));

    // Colours are tried once: if the shader didn't compile, the video plays as recorded and the colour controls hide.
    void ShowEdit()
    {
        if (clip == null) return;
        showing = true;
        var e = clip.Edit;
        Brightness.Value = e.Brightness / 0.3 * 100;
        Contrast.Value = (e.Contrast - 1) / 0.6 * 100;
        Saturation.Value = (e.Saturation - 1) * 100;
        Warmth.Value = e.Warmth / 0.15 * 100;
        Natural.IsChecked = e with { In = 0, Out = 0 } == ClipEdit.None;
        showing = false;
        colour.Set(e);
        Player.Effect = ColourEffect.Shader == null ? null : colour;
        SeekRange();
        Colours.Visibility = ColourEffect.Shader == null ? Visibility.Collapsed : Visibility.Visible;
        ShotTrim.IsEnabled = clip.Events?.Shot() != null;
        GoalTrim.IsEnabled = clip.Events?.GoalOnly() != null;
        TrimText.Text = e.In == 0 && e.Out == 0 ? "Whole clip"
            : $"{e.In:0.0} s to {(e.Out > 0 ? $"{e.Out:0.0} s" : "the end")}";
        UndoButton.IsEnabled = undo.Count > 0 && !saving;
        RedoButton.IsEnabled = redo.Count > 0 && !saving;
        ResetButton.IsEnabled = e != ClipEdit.None && !saving;
        SaveButton.IsEnabled = e != clip.Saved && !saving && Training.Ffmpeg != null;
        if (!saving) EditNote.Text = Training.Ffmpeg == null ? "Saving into the clip needs ffmpeg, which isn't installed. Edits still play here."
            : e != clip.Saved ? "Unsaved changes. They play here; Save writes them into the clip."
            : e == ClipEdit.None ? "This is the clip as recorded." : "Saved into the clip.";
    }

    void OnOpened(object sender, RoutedEventArgs e)
    {
        SeekRange();
        Player.Position = TimeSpan.FromSeconds(clip?.Edit.In ?? 0);
        ShowPosition();
    }

    void OnPlay(object sender, RoutedEventArgs e)
    {
        if (playing) { Pause(); return; }
        if (clip == null) return;
        var (from, to) = Span();
        if (Player.Position.TotalSeconds < from || Player.Position.TotalSeconds >= to - 0.05) Player.Position = TimeSpan.FromSeconds(from);
        Player.Play();
        (playing, PlayButton.Content) = (true, "\uE769"); // the pause icon
        ticker.Start();
    }

    void Pause()
    {
        Player.Pause();
        (playing, PlayButton.Content) = (false, "\uE768"); // the play icon
        ticker.Stop();
    }

    // The trimmed part of the clip, in seconds.
    (double From, double To) Span()
    {
        var e = clip?.Edit ?? ClipEdit.None;
        return (e.In, e.Out > 0 ? e.Out : Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalSeconds : double.MaxValue);
    }

    void OnTick()
    {
        var (from, to) = Span();
        // Also back to the start of the cut if the timeline was clicked before it while playing.
        if (playing && (Player.Position.TotalSeconds >= to || Player.Position.TotalSeconds < from - 0.1)) OnEnded(this, new RoutedEventArgs());
        ShowPosition();
    }

    void ShowPosition()
    {
        seeking = true;
        Seek.Value = Player.Position.TotalSeconds;
        seeking = false;
        if (EditPanel.IsVisible && Length > 0) Canvas.SetLeft(Playhead, X(Player.Position.TotalSeconds) - 1);
    }

    // While the knob is dragged the video is paused and seeks at most every 80 ms: a seek on every pixel of the drag
    // queues more frames than the player can decode, and it freezes.
    bool scrubbing, wasPlaying;
    DateTime lastSeek;

    void OnSeek(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (seeking || scrubbing && DateTime.Now - lastSeek < TimeSpan.FromMilliseconds(80)) return;
        lastSeek = DateTime.Now;
        Player.Position = TimeSpan.FromSeconds(e.NewValue);
    }

    void OnScrubStart(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        (scrubbing, wasPlaying) = (true, playing);
        Pause();
    }

    void OnScrubEnd(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        scrubbing = false;
        Player.Position = TimeSpan.FromSeconds(Seek.Value); // where the knob was let go
        if (wasPlaying) OnPlay(sender, e);
    }

    // Clips loop until paused.
    void OnEnded(object sender, RoutedEventArgs e)
    {
        Player.Position = TimeSpan.FromSeconds(clip?.Edit.In ?? 0);
        if (playing) Player.Play();
        ShowPosition();
    }

    void OnVideoClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) OnFullScreen(sender, e);
        else OnPlay(sender, e);
    }

    void OnFavourite(object sender, RoutedEventArgs e)
    {
        if (clip == null) return;
        clip.Favourite = !clip.Favourite;
        SaveNow(true);
        ShowClips(clip.File); // so the list's star changes too
    }

    void OnFavouritesOnly(object sender, RoutedEventArgs e) => ShowClips(clip?.File);

    void OnEdit(object sender, RoutedEventArgs e)
    {
        EditPanel.Visibility = EditPanel.IsVisible ? Visibility.Collapsed : Visibility.Visible;
        SeekRange();
    }

    // The player's slider covers only the cut part, so a trimmed clip looks like its own video. The Edit panel's timeline
    // shows the whole recording.
    void SeekRange()
    {
        ShowTimeline();
        if (Length == 0) return;
        var (from, to) = Span();
        seeking = true; // changing the range moves the knob, which isn't a seek
        (Seek.Minimum, Seek.Maximum) = (from, Math.Max(from + 0.1, Math.Min(to, Length)));
        seeking = false;
    }

    double Length => Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalSeconds : 0;
    double X(double seconds) => Length == 0 ? 0 : Math.Clamp(seconds / Length, 0, 1) * TimelineCanvas.ActualWidth;
    double AtMouse() => Math.Clamp(Mouse.GetPosition(TimelineCanvas).X / Math.Max(1, TimelineCanvas.ActualWidth), 0, 1) * Length;

    void ShowTimeline()
    {
        var on = clip != null && Length > 0;
        KeptBand.Visibility = InHandle.Visibility = OutHandle.Visibility = Playhead.Visibility = on ? Visibility.Visible : Visibility.Hidden;
        if (!on) return;
        var (from, to) = (clip!.Edit.In, clip.Edit.Out > 0 ? clip.Edit.Out : Length);
        Canvas.SetLeft(KeptBand, X(from));
        KeptBand.Width = Math.Max(0, X(to) - X(from));
        Canvas.SetLeft(InHandle, X(from) - 6);
        Canvas.SetLeft(OutHandle, X(to) - 6);
        Canvas.SetLeft(Playhead, X(Player.Position.TotalSeconds) - 1);
    }

    void OnTimelineSize(object sender, SizeChangedEventArgs e) => ShowTimeline();

    void OnTimelineClick(object sender, MouseButtonEventArgs e)
    {
        if (clip == null || Length == 0) return;
        Player.Position = TimeSpan.FromSeconds(AtMouse());
        ShowPosition();
    }

    void OnHandleStart(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e) => Pause();

    // Dragging a handle moves that end of the cut and shows the frame there.
    void OnHandleDrag(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (clip == null || Length == 0) return;
        var t = Math.Round(AtMouse(), 1);
        var edit = clip.Edit;
        var end = edit.Out > 0 ? edit.Out : Length;
        if ((string)((FrameworkElement)sender).Tag == "in") edit = edit with { In = Math.Clamp(t, 0, Math.Max(0, end - 0.5)) };
        else
        {
            t = Math.Clamp(t, Math.Min(edit.In + 0.5, Length), Length); // a cut that starts near the end can't go past it
            edit = edit with { Out = t >= Length - 0.05 ? 0 : t };
        }
        Change(edit, burst: true);
        Player.Position = TimeSpan.FromSeconds(sender == InHandle ? clip.Edit.In : Math.Min(t, Length - 0.05));
        ShowPosition();
    }

    void OnTrim(object sender, RoutedEventArgs e)
    {
        if (clip == null) return;
        var at = Player.Position.TotalSeconds;
        var (from, to) = ((string)((FrameworkElement)sender).Tag switch
        {
            "shot" => clip.Events?.Shot(),
            "goal" => clip.Events?.GoalOnly(),
            "in" => (at, clip.Edit.Out > at ? clip.Edit.Out : 0),
            "out" => (clip.Edit.In < at ? clip.Edit.In : 0, at),
            _ => (0.0, 0.0),
        }) ?? (0.0, 0.0);
        Change(clip.Edit with { In = Math.Round(from, 1), Out = Math.Round(to, 1) });
        Player.Position = TimeSpan.FromSeconds(clip.Edit.In);
        ShowPosition();
    }

    // A preset look: brightness, contrast, colour and warmth, in the sliders' -100 to 100.
    void OnLook(object sender, RoutedEventArgs e)
    {
        var v = ((string)((FrameworkElement)sender).Tag).Split(',').Select(double.Parse).ToArray();
        (Brightness.Value, Contrast.Value, Saturation.Value, Warmth.Value) = (v[0], v[1], v[2], v[3]);
        ((RadioButton)sender).IsChecked = true;
    }

    void OnColour(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (showing || clip == null) return;
        Change(clip.Edit with
        {
            Brightness = Brightness.Value / 100 * 0.3, Contrast = 1 + Contrast.Value / 100 * 0.6,
            Saturation = 1 + Saturation.Value / 100, Warmth = Warmth.Value / 100 * 0.15,
        }, burst: true);
    }

    // One undo step per change; a slider drag or a look (four sliders at once) is one step, since saver is still
    // counting down from the previous change.
    void Change(ClipEdit next, bool burst = false)
    {
        if (clip == null || next == clip.Edit) return;
        if (!burst || !saver.IsEnabled) undo.Push(clip.Edit);
        redo.Clear();
        clip.Edit = next;
        Edited();
    }

    void Edited()
    {
        ShowEdit();
        saver.Stop();
        saver.Start(); // a slider drag saves once, when it stops
    }

    void OnUndo(object sender, RoutedEventArgs e) => Step(undo, redo);
    void OnRedo(object sender, RoutedEventArgs e) => Step(redo, undo);

    void Step(Stack<ClipEdit> from, Stack<ClipEdit> to)
    {
        if (clip == null || from.Count == 0) return;
        to.Push(clip.Edit);
        clip.Edit = from.Pop();
        saver.Stop(); // so the next slider change is its own step
        Edited();
    }

    void OnReset(object sender, RoutedEventArgs e) => Change(ClipEdit.None);

    async void OnSaveVideo(object sender, RoutedEventArgs e)
    {
        if (clip == null || saving || sample) return;
        var editing = clip;
        saving = true;
        Pause();
        Player.Source = null; // lets go of the file, so it can be replaced
        ShowEdit();
        EditNote.Text = editing.Edit == ClipEdit.None ? "Putting the original back…" : "Saving the edit into the clip…";
        try
        {
            await Training.SaveVideo(editing);
            EditNote.Text = editing.Edit == ClipEdit.None ? "The clip is back to how it was recorded." : "Saved into the clip. Reset and Save puts the original back.";
        }
        catch (Exception ex) { EditNote.Text = $"Couldn't save the clip: {ex.Message}"; }
        saving = false;
        if (clip != editing) return;
        ShowEdit();
        Player.Source = new Uri(clip.Source);
        Player.Play();
        Pause();
    }

    void SaveNow(bool always = false)
    {
        if (clip == null || sample || !(always || saver.IsEnabled)) return;
        saver.Stop();
        try { Training.SaveEdits(clip); }
        catch (Exception ex) { EditNote.Text = $"Couldn't save the edit: {ex.Message}"; }
    }

    void OnFullScreen(object sender, RoutedEventArgs e)
    {
        if (clip == null) return;
        var was = playing;
        Pause();
        var at = new ClipWindow(clip, Player.Position, Window.GetWindow(this)).Watch(was);
        Player.Position = at;
        ShowPosition();
    }

    void OnRename(object sender, RoutedEventArgs e)
    {
        if (clip == null || saving) return;
        (RenameBox.Text, RenameBox.Visibility, ClipName.Visibility) = (clip.Shot, Visibility.Visible, Visibility.Collapsed);
        RenameBox.Focus();
        RenameBox.SelectAll();
    }

    void OnRenameKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) RenameBox.Text = clip?.Shot ?? "";
        if (e.Key is Key.Enter or Key.Escape) Keyboard.ClearFocus(); // → OnRenameDone
    }

    void OnRenameDone(object sender, RoutedEventArgs e)
    {
        if (RenameBox.Visibility != Visibility.Visible) return;
        (RenameBox.Visibility, ClipName.Visibility) = (Visibility.Collapsed, Visibility.Visible);
        if (clip == null || sample || RenameBox.Text.Trim() == clip.Shot) return;
        SaveNow();
        Pause();
        Player.Source = null; // lets go of the file, so it can be moved
        try { clip = Training.Rename(clip, RenameBox.Text); }
        catch (Exception ex) { ClipInfo.Text = $"Couldn't rename the clip: {ex.Message}"; }
        ShowClips(clip.File);
    }

    void OnDelete(object sender, RoutedEventArgs e)
    {
        if (clip == null) return;
        saver.Stop();
        Player.Source = null; // lets go of the file
        try { Training.Delete(clip); }
        catch (Exception ex) { ClipInfo.Text = $"Couldn't delete the clip: {ex.Message}"; return; }
        clip = null;
        ShowClips();
    }

    void OnClipsFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Training.ClipsFolder);
        Tweaks.OpenAsYou(Training.ClipsFolder);
    }

    Brush Paint(string name) => (Brush)FindResource(name);

    // The Library tab with made-up shot details and a level picked.
    public void ShowLibrarySample()
    {
        sample = true;
        LibraryTabButton.IsChecked = true;
        string[] categories = ["Shooting", "Aerials & Reads", "Defense"];
        var shots = Training.BuiltInShots;
        List<ShotInfo> library = [.. shots.Select((name, i) => new ShotInfo(i, name, categories[Math.Min(2, i / 5)], 1 + i % 4,
            "Read the ball off the wall, get there early and put it in the open corner.")
        {
            // Made-up throws: from a corner or the side, towards the middle, some along the ground and some as lobs.
            Car = [i % 3 * 600 - 600, 600 + i % 4 * 300, Math.PI / 2], Ball = [i % 2 == 0 ? 2800 : -2800, 2400 + i % 3 * 500, 93],
            Path = [.. Enumerable.Range(0, 16).Select(t => t * 0.15).Select(t => new[]
                { (i % 2 == 0 ? 2800 : -2800) * (1 - t / 2.6), 2400 + i % 3 * 500 + 500 * t, Math.Max(93, 93 + (i % 3) * 600 * t - 325 * t * t) })],
            Opponents = i % 2 == 0 ? new List<double[]> { new[] { 300.0, 4900, -Math.PI / 2 } } : [],
        })];
        foreach (RadioButton pick in LevelPicker.Children) pick.IsChecked = (int)pick.Tag == 2;
        var at = new DateTime(2026, 10, 21, 20, 10, 0);
        List<Attempt> all = [.. Enumerable.Range(0, 60).Select(i => new Attempt(at.AddMinutes(i / 3.0), "a", shots[i * 7 % shots.Length], i % 5 != i % 3))];
        ShowLibrary(library, Training.PerShot(all, shots));
    }

    // The Stats tab with a made-up history.
    public void ShowStatsSample()
    {
        sample = true;
        StatsTabButton.IsChecked = true;
        var at = new DateTime(2026, 10, 21, 20, 10, 0);
        var shots = Training.BuiltInShots;
        List<Attempt> all = [.. Enumerable.Range(0, 60).Select(i => new Attempt(at.AddMinutes(i / 3.0), i < 30 ? "a" : "b", shots[i * 7 % shots.Length], i % 5 != i % 3))];
        var last = all.Where(a => a.Session == "b").ToList();
        ShowStats(all, Training.PerShot(all, shots), (last, 3));
    }

    // For the CI snapshot, which has no trainer installed: the page mid-session.
    public void ShowSample()
    {
        sample = true;
        Buttons.Visibility = Keys.Visibility = WithGameRow.Visibility = WithGamePanel.Visibility = Controls.Visibility = Visibility.Visible;
        (State.Text, StateDot.Fill) = ("Training is running.", Paint("Good"));
        ShowSettings(new JsonObject
        {
            ["bot_skill"] = 70.0, ["head_start"] = 0.8, ["countdown"] = 3.0, ["ball_speed"] = 100.0, ["shot_time"] = 120.0, ["variation"] = 20.0,
            ["order"] = "Random", ["show_path"] = true, ["infinite_boost"] = true,
            ["shots"] = new JsonArray(true, true, true, true, false, false, false, false, true, true),
        });
    }

    // The Clips tab with made-up clips; there's no video on CI, so the player stays black.
    public void ShowClipsSample()
    {
        sample = true;
        ClipsTabButton.IsChecked = true;
        var now = new DateTime(2026, 10, 20, 21, 14, 0);
        ShotClip[] clips =
        [
            new(@"C:\clips\a.mp4", "6. Wall air dribble", "GOAL!", now)
            {
                Favourite = true, Edit = new(4.5, 16.0, 0.015, 1.12, 1.5, 0.0075), Events = new(20, [5.5, 9.1], 14, 5),
            },
            new(@"C:\clips\b.mp4", "4. High pass: fast aerial", "GOAL!", now.AddMinutes(-6)) { Favourite = true },
            new(@"C:\clips\c.mp4", "9. Cross from the corner", "Missed", now.AddMinutes(-11)),
        ];
        ClipList.ItemsSource = clips;
        (NoClips.Visibility, ClipView.Visibility) = (Visibility.Collapsed, Visibility.Visible);
        (ClipName.Text, ClipInfo.Text) = (clips[0].Shot, clips[0].Info);
        clip = clips[0];
        ShowFavourite();
        ShowEdit();
        EditPanel.Visibility = Visibility.Visible;
    }
}
