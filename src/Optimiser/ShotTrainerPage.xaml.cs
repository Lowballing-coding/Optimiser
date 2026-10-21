using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Optimiser;

public partial class ShotTrainerPage : UserControl
{
    bool loading = true, sample; // loading until the settings are shown, so setting up the sliders saves nothing

    public ShotTrainerPage()
    {
        InitializeComponent();
        for (var i = 0; i < Training.Shots.Length; i++)
        {
            var name = Training.Shots[i];
            var row = new DockPanel { Margin = new Thickness(0, 0, i % 2 == 0 ? 40 : 0, 12) };
            var box = new CheckBox { Style = (Style)FindResource("Switch"), Tag = i };
            box.Click += OnShot;
            AutomationProperties.SetName(box, name);
            DockPanel.SetDock(box, Dock.Right);
            row.Children.Add(box);
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            ShotList.Children.Add(row);
        }
        ticker.Tick += (_, _) => OnTick();
        saver.Tick += (_, _) => SaveNow();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) { Pause(); SaveNow(); }
            else if (!sample) Show();
        };
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
        ShowSettings(Training.Load());
        if (ClipsTab.IsVisible) ShowClips();
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
        InfiniteBoost.IsChecked = Training.Flag(s, "infinite_boost", false);
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
        if (ClipsTab == null) return; // the first tab is checked while the page loads
        var clips = sender == ClipsTabButton;
        TrainingTab.Visibility = clips ? Visibility.Collapsed : Visibility.Visible;
        ClipsTab.Visibility = clips ? Visibility.Visible : Visibility.Collapsed;
        if (clips) ShowClips();
        else { Pause(); SaveNow(); }
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
            first.IsChecked = true;
    }

    ShotClip? clip;
    bool playing, seeking, showing; // showing: the edit controls are being set from a clip, so changing them saves nothing
    readonly ColourEffect colour = new();
    readonly System.Windows.Threading.DispatcherTimer ticker = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly System.Windows.Threading.DispatcherTimer saver = new() { Interval = TimeSpan.FromMilliseconds(400) };

    void OnClip(object sender, RoutedEventArgs e) => Pick((ShotClip)((FrameworkElement)sender).DataContext);

    void Pick(ShotClip? next)
    {
        SaveNow();
        clip = next;
        if (clip == null) { (ClipName.Text, ClipInfo.Text) = ("", ""); return; }
        (ClipName.Text, ClipInfo.Text) = (clip.Shot, clip.Info);
        ShowFavourite();
        ShowEdit();
        Player.Source = new Uri(clip.File);
        Player.Play(); // then straight to Pause, which shows the first frame
        Pause();
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
        Colours.Visibility = ColourEffect.Shader == null ? Visibility.Collapsed : Visibility.Visible;
        ShotTrim.IsEnabled = clip.Events?.Shot() != null;
        GoalTrim.IsEnabled = clip.Events?.GoalOnly() != null;
        TrimText.Text = e.In == 0 && e.Out == 0 ? "Whole clip"
            : $"{e.In:0.0} s to {(e.Out > 0 ? $"{e.Out:0.0} s" : "the end")}";
    }

    void OnOpened(object sender, RoutedEventArgs e)
    {
        if (Player.NaturalDuration.HasTimeSpan) Seek.Maximum = Player.NaturalDuration.TimeSpan.TotalSeconds;
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
        (playing, PlayButton.Content) = (true, "Pause");
        ticker.Start();
    }

    void Pause()
    {
        Player.Pause();
        (playing, PlayButton.Content) = (false, "Play");
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
        if (playing && Player.Position.TotalSeconds >= Span().To) OnEnded(this, new RoutedEventArgs());
        ShowPosition();
    }

    void ShowPosition()
    {
        seeking = true;
        Seek.Value = Player.Position.TotalSeconds;
        seeking = false;
    }

    void OnSeek(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!seeking) Player.Position = TimeSpan.FromSeconds(e.NewValue);
    }

    void OnEnded(object sender, RoutedEventArgs e)
    {
        Pause();
        Player.Position = TimeSpan.FromSeconds(clip?.Edit.In ?? 0);
        ShowPosition();
    }

    void OnVideoClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
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

    void OnEdit(object sender, RoutedEventArgs e) =>
        EditPanel.Visibility = EditPanel.IsVisible ? Visibility.Collapsed : Visibility.Visible;

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
        clip.Edit = clip.Edit with { In = Math.Round(from, 1), Out = Math.Round(to, 1) };
        Edited();
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
        clip.Edit = clip.Edit with
        {
            Brightness = Brightness.Value / 100 * 0.3, Contrast = 1 + Contrast.Value / 100 * 0.6,
            Saturation = 1 + Saturation.Value / 100, Warmth = Warmth.Value / 100 * 0.15,
        };
        Edited();
    }

    void Edited()
    {
        ShowEdit();
        saver.Stop();
        saver.Start(); // a slider drag saves once, when it stops
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
