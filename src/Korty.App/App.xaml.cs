using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Korty.App;

public partial class App : Application
{
    private AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? dataDir = null; bool? sim = null; string? shotDir = null;
        for (int i = 0; i < e.Args.Length; i++)
        {
            switch (e.Args[i])
            {
                case "--data-dir" when i + 1 < e.Args.Length: dataDir = e.Args[++i]; break;
                case "--simulate": sim = true; break;
                case "--screenshots" when i + 1 < e.Args.Length: shotDir = e.Args[++i]; sim = true; break;
            }
        }

        DispatcherUnhandledException += (_, args) =>
        {
            _host?.Log.Error("crash", args.Exception.ToString());
            MessageBox.Show("Произошла непредвиденная ошибка. Устройство не затронуто.\n\n" + args.Exception.Message + "\n\nПодробности в журнале.",
                "KORTY Keyboard Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        _host = new AppHost(dataDir, sim);
        var vm = new ViewModels.MainViewModel(_host);
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Show();
        _host.Start();

        if (shotDir != null) _ = ScreenshotRunner.RunAsync(window, vm, _host, shotDir);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}
