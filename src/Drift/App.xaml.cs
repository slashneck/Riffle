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

        instance = SingleInstance.TryClaim(() => Dispatcher.BeginInvoke(() => main?.ShowFlyout()));
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
        main.ShowFlyout();
        player.AttachMediaControls(new WindowInteropHelper(main).Handle);

        widget = new TaskbarWidget(player, main);
        widget.QuitRequested += Quit;
        widget.Start();

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
