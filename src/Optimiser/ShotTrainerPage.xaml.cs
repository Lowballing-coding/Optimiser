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
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) Pause();
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
        else Pause();
    }

    void ShowClips()
    {
        Pause();
        Player.Source = null;
        var clips = Training.Clips();
        ClipList.ItemsSource = clips;
        NoClips.Visibility = clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipView.Visibility = clips.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (clips.Count == 0) return;
        ClipList.UpdateLayout(); // so the first row exists to be picked
        if (ClipList.ItemContainerGenerator.ContainerFromIndex(0) is ContentPresenter row
            && VisualTreeHelper.GetChildrenCount(row) > 0 && VisualTreeHelper.GetChild(row, 0) is RadioButton first)
            first.IsChecked = true;
    }

    ShotClip? clip;
    bool playing;

    void OnClip(object sender, RoutedEventArgs e)
    {
        clip = (ShotClip)((FrameworkElement)sender).DataContext;
        (ClipName.Text, ClipInfo.Text) = (clip.Shot, clip.Info);
        Player.Source = new Uri(clip.File);
        Player.Play(); // then straight to Pause, which shows the first frame
        Pause();
    }

    void OnPlay(object sender, RoutedEventArgs e)
    {
        if (playing) { Pause(); return; }
        Player.Play();
        (playing, PlayButton.Content) = (true, "Pause");
    }

    void Pause()
    {
        Player.Pause();
        (playing, PlayButton.Content) = (false, "Play");
    }

    void OnEnded(object sender, RoutedEventArgs e)
    {
        Pause();
        Player.Position = TimeSpan.Zero;
    }

    void OnDelete(object sender, RoutedEventArgs e)
    {
        if (clip == null) return;
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
            new(@"C:\clips\a.mp4", "6. Wall air dribble", "GOAL!", now),
            new(@"C:\clips\b.mp4", "4. High pass: fast aerial", "GOAL!", now.AddMinutes(-6)),
            new(@"C:\clips\c.mp4", "9. Cross from the corner", "Missed", now.AddMinutes(-11)),
        ];
        ClipList.ItemsSource = clips;
        (NoClips.Visibility, ClipView.Visibility) = (Visibility.Collapsed, Visibility.Visible);
        (ClipName.Text, ClipInfo.Text) = (clips[0].Shot, clips[0].Info);
    }
}
