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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        instance = SingleInstance.TryClaim(() => Dispatcher.BeginInvoke(() => main?.ShowFlyout(widget?.Bounds)));
        if (instance is null)
        {
            Shutdown();
            return;
        }

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

        // started with Windows: just the tile, no window popping up
        if (!e.Args.Contains(Autostart.Flag) || !player.ShowTile)
            main.ShowFlyout(widget.Bounds);

        SessionEnding += (_, _) => player.Dispose();

        try
        {
            await host.StartAsync();
            await player.StartUpAsync(resume: true);
        }
        catch (Exception)
        {
            MessageBox.Show("Riffle needs the Microsoft Edge WebView2 Runtime to play music. It comes with Windows 11; install it from Microsoft if it's missing.", "Riffle");
        }
    }

    void Quit()
    {
        widget?.Close();
        main?.CloseForGood();
        player?.Dispose();
        host?.Dispose();
        instance?.Dispose();
        Shutdown();
    }
}
