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
            var outer = new ScrollViewer { Content = new StackPanel { Children = { inner, new Border { Height = 1000 } } } };
            var window = new Window
            {
                Content = outer, Width = 300, Height = 200, Left = -5000, Top = -5000,
                WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false,
            };
            window.Show();
            var moves = 0;
            inner.ScrollChanged += (_, e) => { if (e.VerticalChange != 0) moves++; };

            void Wheel(int notches)
            {
                for (var i = 0; i < notches; i++)
                    inside.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                var until = DateTime.Now.AddSeconds(5);
                do
                {
                    var frame = new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
                    Dispatcher.PushFrame(frame);
                    Thread.Sleep(5);
                } while (SmoothScroll.Busy && DateTime.Now < until);
            }

            Wheel(1);
            results.Add((Math.Abs(inner.VerticalOffset - Math.Min(SmoothScroll.Step(inner), inner.ScrollableHeight)) < 1 && outer.VerticalOffset == 0,
                "the mouse wheel scrolls the part under the mouse"));
            results.Add((moves > 1 || !SystemParameters.ClientAreaAnimation, "and glides there over several frames"));
            Wheel(3);
            results.Add((inner.VerticalOffset == inner.ScrollableHeight && outer.VerticalOffset > 0,
                "then the page around it once that part reaches its end"));
            window.Close();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
        return results;
    }
}
