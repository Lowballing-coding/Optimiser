using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Optimiser.Tests;

// A small list inside a long page, in an off-screen window on its own UI thread, scrolled with made-up wheel notches.
static class ScrollCheck
{
    public static List<(bool Ok, string What)> Run()
    {
        var results = new List<(bool, string)>();
        var ui = new Thread(() =>
        {
            SmoothScroll.Enable();
            var inside = new Border { Height = 300 };
            var inner = new ScrollViewer { Height = 100, Content = inside };
            var below = new Border { Height = 1000 };
            var outer = new ScrollViewer { Content = new StackPanel { Children = { inner, below } } };
            var window = new Window
            {
                Content = outer, Width = 300, Height = 200, Left = -5000, Top = -5000,
                WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false,
            };
            window.Show();
            var moves = 0;
            inner.ScrollChanged += (_, e) => { if (e.VerticalChange != 0) moves++; };

            static void Wheel(UIElement over, int notches)
            {
                for (var i = 0; i < notches; i++)
                    over.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            }
            // Waits in the dispatcher's own loop, so WPF's frame timers keep firing, for up to 5 s.
            static void RunUntil(Func<bool> done)
            {
                var frame = new DispatcherFrame();
                var until = DateTime.Now.AddSeconds(5);
                var poll = new DispatcherTimer(TimeSpan.FromMilliseconds(5), DispatcherPriority.Background,
                    (_, _) => frame.Continue = !done() && DateTime.Now < until, Dispatcher.CurrentDispatcher);
                Dispatcher.PushFrame(frame);
                poll.Stop();
            }

            var glide = Stopwatch.StartNew();
            Wheel(inside, 1);
            RunUntil(() => !SmoothScroll.Busy);
            glide.Stop();
            results.Add((Math.Abs(inner.VerticalOffset - Math.Min(SmoothScroll.Step(inner), inner.ScrollableHeight)) < 1 && outer.VerticalOffset == 0,
                "the mouse wheel scrolls the part under the mouse"));
            results.Add((moves > 1 || !SystemParameters.ClientAreaAnimation,
                $"and glides there over several frames ({moves} frames, {glide.ElapsedMilliseconds} ms)"));
            Wheel(inside, 3);
            RunUntil(() => !SmoothScroll.Busy);
            results.Add((inner.VerticalOffset == inner.ScrollableHeight && outer.VerticalOffset > 0,
                "then the page around it once that part reaches its end"));

            var from = outer.VerticalOffset;
            Wheel(below, 2);
            RunUntil(() => outer.VerticalOffset > from);
            outer.ScrollToVerticalOffset(50); // as if the scrollbar were dragged mid-glide
            RunUntil(() => !SmoothScroll.Busy);
            results.Add((Math.Abs(outer.VerticalOffset - 50) < 1 || !SystemParameters.ClientAreaAnimation,
                "dragging the scrollbar mid-glide wins over the wheel"));
            window.Close();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
        return results;
    }
}
