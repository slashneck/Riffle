using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Drift.Controls;
using Drift.Services;
using Drift.Views;

namespace Drift;

public partial class App : Application
{
    SingleInstance? instance;
    Player? player;
    MainWindow? main;
    TaskbarWidget? widget;

    protected override void OnStartup(StartupEventArgs e)
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

        player = new Player(Settings.Load(), ui);
        LevelBars.Source = player.Engine.Spectrum;

        main = new MainWindow(player);
        main.QuitRequested += Quit;
        player.AttachMediaControls(new WindowInteropHelper(main).EnsureHandle());

        widget = new TaskbarWidget(player, main);
        widget.QuitRequested += Quit;
        widget.Start();

        // started with Windows: just the tile, no window popping up
        bool background = e.Args.Contains(Autostart.Flag);
        if (!background || !player.ShowInTaskbar)
            main.ShowFlyout(widget.Bounds);
        player.StartUp(resume: true);

        SessionEnding += (_, _) => player.Save();
    }

    void Quit()
    {
        widget?.Close();
        main?.CloseForGood();
        player?.Dispose();
        instance?.Dispose();
        Shutdown();
    }
}
