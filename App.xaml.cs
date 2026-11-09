using System.IO;
using System.Windows;

namespace DynamicIsland;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "Local\\DynamicIsland.SingleInstance", out var created);
        _ownsMutex = created;
        if (!created)
        {
            MessageBox.Show("Dynamic Island уже запущен.", "Dynamic Island", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            File.AppendAllText(Path.Combine(Services.AppPaths.DataDirectory, "errors.log"),
                $"{DateTime.Now:O} {args.Exception}\n");
            args.Handled = true;
        };
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
