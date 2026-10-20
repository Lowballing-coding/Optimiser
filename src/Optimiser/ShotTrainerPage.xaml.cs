using System.Diagnostics;
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
        IsVisibleChanged += (_, _) => { if (IsVisible && !sample) Show(); };
    }

    // Re-read each time the page opens: the trainer's own window may have changed the file.
    void Show()
    {
        if (!Training.Installed)
        {
            (State.Text, StateDot.Fill) = ($"The shot trainer isn't in {Training.Folder}.", Paint("Muted"));
            Buttons.Visibility = Keys.Visibility = Controls.Visibility = Visibility.Collapsed;
            return;
        }
        Buttons.Visibility = Keys.Visibility = Controls.Visibility = Visibility.Visible;
        (State.Text, StateDot.Fill) = Training.Running ? ("Training is running.", Paint("Good"))
                                                       : ("Not running. Start it here or from the RL Trainer shortcut.", Paint("Muted"));
        ShowSettings(Training.Load());
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

    void OnSwitch(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        var key = (string)box.Tag;
        Change(s => s[key] = key == "order" ? (box.IsChecked == true ? "Random" : "In order") : (JsonNode)(box.IsChecked == true));
    }

    void OnShot(object sender, RoutedEventArgs e) => Change(s =>
        s["shots"] = new JsonArray([.. ShotList.Children.Cast<DockPanel>().Select(r => (JsonNode?)(((CheckBox)r.Children[0]).IsChecked == true))]));

    void OnStart(object sender, RoutedEventArgs e)
    {
        if (Training.Running)
        {
            (State.Text, StateDot.Fill) = ("Training is already running.", Paint("Good"));
            return;
        }
        Training.Start();
        (State.Text, StateDot.Fill) = ("Starting Rocket League with the trainer. The first shot comes after the kickoff.", Paint("Warn"));
    }

    void OnRetry(object sender, RoutedEventArgs e) => Command("retry");

    void OnNext(object sender, RoutedEventArgs e) => Command("next");

    void Command(string command)
    {
        if (sample) return;
        try { Training.Send(command); }
        catch (Exception e) { (State.Text, StateDot.Fill) = ($"Couldn't reach the trainer: {e.Message}", Paint("Warn")); }
    }

    void OnFolder(object sender, RoutedEventArgs e) => Tweaks.OpenAsYou(Training.Folder);

    Brush Paint(string name) => (Brush)FindResource(name);

    // For the CI snapshot, which has no trainer installed: the page mid-session.
    public void ShowSample()
    {
        sample = true;
        Buttons.Visibility = Keys.Visibility = Controls.Visibility = Visibility.Visible;
        (State.Text, StateDot.Fill) = ("Training is running.", Paint("Good"));
        ShowSettings(new JsonObject
        {
            ["bot_skill"] = 70, ["head_start"] = 0.8, ["countdown"] = 3, ["ball_speed"] = 100, ["shot_time"] = 120, ["variation"] = 20,
            ["order"] = "Random", ["show_path"] = true,
            ["shots"] = new JsonArray(true, true, true, true, false, false, false, false, true, true),
        });
    }
}
