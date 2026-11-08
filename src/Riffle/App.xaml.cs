using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Riffle.Controls;
using Riffle.Core;
using Riffle.Playback;
using Riffle.Services;
using Riffle.Views;

namespace Riffle;

public partial class App : Application
{
    SingleInstance? instance;
    PlayerHost? host;
    Player? player;
    MainWindow? main;
    TaskbarWidget? widget;
    OverlayWindow? overlay;
    Shortcuts? shortcuts;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length >= 3 && e.Args[0] == Updater.ApplyFlag)
        {
            Shutdown(Updater.Apply(e.Args[1], int.TryParse(e.Args[2], out int old) ? old : 0));
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == Installation.UninstallFlag)
        {
            Shutdown(Installation.Uninstall());
            return;
        }

        instance = SingleInstance.TryClaim(() => Dispatcher.BeginInvoke(() => main?.ShowFlyout(widget?.Bounds)));
        if (instance is null)
        {
            Shutdown();
            return;
        }

        // a bug in one click shouldn't take the music down with it
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash(args.Exception);
            args.SetObserved();
        };

        Updater.CleanUp();
        RegisterApplicationRestart($"{Autostart.Flag} {RecoveredFlag}", 0);

        var ui = new DispatcherSynchronizationContext(Dispatcher);
        SynchronizationContext.SetSynchronizationContext(ui);

        var settings = Settings.Load();
        host = new PlayerHost();
        player = new Player(settings, Library.Load(), host, ui);
        LevelBars.Source = () => player.Spectrum;

        main = new MainWindow(player);
        main.QuitRequested += Quit;
        player.AttachMediaControls(new WindowInteropHelper(main).EnsureHandle());

        widget = new TaskbarWidget(player, main);
        widget.QuitRequested += Quit;
        widget.Start();

        main.AttachUpdates(new UpdateService(() => player.CheckForUpdates, () => player.IsPlaying, Quit));

        overlay = new OverlayWindow(player);
        shortcuts = new Shortcuts(new WindowInteropHelper(main).Handle, settings, new Dictionary<string, Action>
        {
            ["Show"] = overlay.ShowBriefly,
            ["Toggle"] = player.Toggle,
            ["Next"] = player.Next,
            ["Previous"] = player.Previous,
        });
        main.AttachShortcuts(shortcuts);

        // started with Windows: just the tile, no window popping up
        if (!e.Args.Contains(Autostart.Flag) || !player.ShowTile)
            main.ShowFlyout(widget.Bounds);

        SessionEnding += (_, _) => player.Dispose();

        try
        {
            await host.StartAsync();
            await player.StartUpAsync(resume: true, recovered: e.Args.Contains(RecoveredFlag));
        }
        catch (Exception)
        {
            MessageBox.Show("Riffle needs the Microsoft Edge WebView2 Runtime to play music. It comes with Windows 11; install it from Microsoft if it's missing.", "Riffle");
        }
    }

    const string RecoveredFlag = "--recovered";

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int RegisterApplicationRestart(string commandLine, int flags);

    static void LogCrash(Exception error)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.AppendAllText(Path.Combine(Settings.Folder, "errors.log"), $"{DateTime.Now:u} {error}\n\n");
        }
        catch (Exception)
        {
        }
    }

    void Quit()
    {
        shortcuts?.Dispose();
        overlay?.Close();
        widget?.Close();
        main?.CloseForGood();
        player?.Dispose();
        int browser = host?.BrowserProcess ?? 0;
        host?.Dispose();
        instance?.Dispose();
        Shutdown();

        // The player's browser normally closes with its window. If it hangs on, it goes too.
        if (browser > 0)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                try { System.Diagnostics.Process.GetProcessById(browser).Kill(entireProcessTree: true); }
                catch (Exception) { }
            });
        }
    }
}
