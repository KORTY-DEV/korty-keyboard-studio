using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Korty.App.ViewModels;

namespace Korty.App;

/// <summary>
/// `--screenshots &lt;dir&gt;` renders every page of the running window to PNG (simulation mode only) and exits.
/// Used for reproducible visual checks; it exercises the real UI code paths but sends nothing anywhere.
/// </summary>
public static class ScreenshotRunner
{
    private static readonly string[] Slugs = { "overview", "screen", "lighting", "keyboard", "profiles", "diagnostics", "simulator", "safety" };

    public static async Task RunAsync(Window window, MainViewModel vm, AppHost host, string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            window.Top = 0; window.Height = 1240;
            await Delay(1200);
            // Demo content: an animated GIF generated locally, so the animation path is exercised end to end.
            var gif = Path.Combine(Path.GetTempPath(), "korty-demo.gif");
            MakeDemoGif(gif);
            vm.Screen.AddImageFromFile(gif);
            vm.Screen.MoveDownCommand.Execute(null);   // image below the default text layer
            vm.Screen.Name = "Демо: бегущий блок";
            vm.Screen.SaveCommand.Execute(null);
            vm.Screen.Selected = vm.Screen.Layers.FirstOrDefault(l => l.IsText);
            if (vm.Screen.Selected != null) { vm.Screen.Selected.Text = "KORTY"; vm.Screen.Selected.Y = 44; vm.Screen.Selected.FontSize = 26; }
            vm.Screen.MaxFrames = 12;
            await Delay(500);
            vm.Screen.PlayCommand.Execute(null);
            await Delay(700);
            // Give the dashboard something to show: a simulated upload.
            vm.Screen.PrepareCommand.Execute(null);
            await Delay(300);
            if (vm.Screen.UploadCommand.CanExecute(null)) { vm.Screen.UploadCommand.Execute(null); await Delay(1500); }
            vm.Rgb.ApplyCommand.Execute(null);
            await Delay(300);
            foreach (var item in vm.Nav)
            {
                vm.Selected = item;
                if (item.Page is ProfilesViewModel p) p.Usb.BuildPlan();
                await Delay(700);
                var idx = Array.IndexOf(vm.Nav.ToArray(), item);
                Save(window, Path.Combine(dir, $"{idx + 1:00}-{(idx < Slugs.Length ? Slugs[idx] : "page")}.png"));
            }
            host.Log.Info("screenshots", "done: " + dir);
        }
        catch (Exception ex) { host.Log.Error("screenshots", ex.ToString()); }
        finally { Application.Current.Shutdown(); }
    }

    private static void MakeDemoGif(string path)
    {
        var enc = new GifBitmapEncoder();
        for (int i = 0; i < 16; i++)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(20, 24, 40), Color.FromRgb(90, 30, 10), 90), null, new Rect(0, 0, 96, 96));
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 90, 31)), null, new Point(10 + i * 5.2, 48 + 24 * Math.Sin(i / 16.0 * 6.283)), 12, 12);
            }
            var rtb = new RenderTargetBitmap(96, 96, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            enc.Frames.Add(BitmapFrame.Create(rtb));
        }
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static Task Delay(int ms)
    {
        var tcs = new TaskCompletionSource();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); tcs.SetResult(); };
        t.Start();
        return tcs.Task;
    }

    private static void Save(Window w, string path)
    {
        var content = (FrameworkElement)w.Content;
        int width = (int)content.ActualWidth, height = (int)content.ActualHeight;
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
        rtb.Render(bg);
        rtb.Render(content);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
