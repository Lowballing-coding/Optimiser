using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Optimiser;

// A clip filling the screen, with its trim and colours. Space plays and pauses, the arrows skip 5 s, and Esc, F or a
// double-click goes back.
public class ClipWindow : Window
{
    readonly MediaElement player = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform };
    readonly TextBlock hint = new()
    {
        Text = "Space  play / pause      ←  →  5 s      Esc  back", Foreground = Brushes.White, FontSize = 15,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 40),
        Padding = new Thickness(18, 10, 18, 10), Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0C, 0x0E, 0x12)),
    };
    readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly DispatcherTimer hideHint = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly ShotClip clip;
    readonly TimeSpan start;
    bool playing;

    public ClipWindow(ShotClip clip, TimeSpan at, Window? owner)
    {
        (this.clip, start) = (clip, at);
        (Owner, WindowStyle, ResizeMode, Background, ShowInTaskbar) = (owner, WindowStyle.None, ResizeMode.NoResize, Brushes.Black, false);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; // so it fills the screen Optimiser is on
        WindowState = WindowState.Maximized;
        Title = clip.Shot;
        Cursor = Cursors.None;
        if (ColourEffect.Shader != null)
        {
            var colour = new ColourEffect();
            colour.Set(clip.Edit);
            player.Effect = colour;
        }
        player.Source = new Uri(clip.Source);
        player.MediaOpened += (_, _) => player.Position = start;
        player.MediaEnded += (_, _) => Rewind();
        Content = new Grid { Children = { player, hint } };
        ticker.Tick += (_, _) => { if (player.Position.TotalSeconds >= To) Rewind(); };
        hideHint.Tick += (_, _) => { hideHint.Stop(); hint.Visibility = Visibility.Collapsed; Cursor = Cursors.None; };
        MouseMove += (_, _) => ShowHint();
        MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Close(); else Toggle(); };
        KeyDown += OnKey;
        Closed += (_, _) => { ticker.Stop(); hideHint.Stop(); player.Close(); };
    }

    double To => clip.Edit.Out > 0 ? clip.Edit.Out : player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalSeconds : double.MaxValue;

    // Shows the window until it's closed; returns where the video was, so the page can carry on from there.
    public TimeSpan Watch(bool play)
    {
        player.Play();
        if (play) { playing = true; ticker.Start(); }
        else player.Pause(); // shows the frame it was on
        ShowHint();
        ShowDialog();
        return player.Position;
    }

    void Toggle()
    {
        if (playing) { player.Pause(); ticker.Stop(); }
        else
        {
            if (player.Position.TotalSeconds >= To - 0.05) player.Position = TimeSpan.FromSeconds(clip.Edit.In);
            player.Play();
            ticker.Start();
        }
        playing = !playing;
        ShowHint();
    }

    void Rewind()
    {
        (playing, player.Position) = (false, TimeSpan.FromSeconds(clip.Edit.In));
        player.Pause();
        ticker.Stop();
        ShowHint();
    }

    void ShowHint()
    {
        hint.Visibility = Visibility.Visible;
        Cursor = null;
        hideHint.Stop();
        hideHint.Start();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape or Key.F: Close(); break;
            case Key.Space: Toggle(); break;
            case Key.Left or Key.Right:
                var s = player.Position.TotalSeconds + (e.Key == Key.Left ? -5 : 5);
                player.Position = TimeSpan.FromSeconds(Math.Clamp(s, clip.Edit.In, Math.Max(clip.Edit.In, To - 0.1)));
                ShowHint();
                break;
            default: return;
        }
        e.Handled = true;
    }
}
