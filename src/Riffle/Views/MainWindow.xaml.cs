using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Riffle.Playback;
using Riffle.Shell;
using Riffle.Themes;

namespace Riffle.Views;

public partial class MainWindow : Window
{
    readonly Player player;
    IntPtr handle;
    bool quitting;
    bool holdOpen;
    long hiddenAt;

    public MainWindow(Player player)
    {
        this.player = player;
        InitializeComponent();
        DataContext = player;

        Now.Attach(player);
        QueueView.Attach(player);
        LibraryView.Attach(player, this);
        SettingsView.Attach(player, this);

        player.PropertyChanged += OnPlayerChanged;
        (player.HasQueue || player.HasSources ? NavNow : NavLibrary).IsChecked = true;
        if (!player.HasQueue && player.HasSources)
            NavLibrary.IsChecked = true;

        SourceInitialized += (_, _) => StyleFrame();
        Deactivated += (_, _) => HideFlyout();
        PreviewKeyDown += OnKey;
    }

    public event Action? QuitRequested;

    public void RequestQuit() => QuitRequested?.Invoke();

    // lets a window of ours take focus (signing in) without the flyout closing under it
    public void HoldOpen(bool hold) => holdOpen = hold;

    public void ShowPage(string page)
    {
        (page switch
        {
            "queue" => NavQueue,
            "library" => NavLibrary,
            "settings" => NavSettings,
            _ => NavNow,
        }).IsChecked = true;
    }

    void OnNav(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string page } || Pages is null)
            return;

        FrameworkElement show = page switch
        {
            "queue" => QueueView,
            "library" => LibraryView,
            "settings" => SettingsView,
            _ => Now,
        };

        foreach (FrameworkElement child in Pages.Children)
            child.Visibility = child == show ? Visibility.Visible : Visibility.Collapsed;

        var time = TimeSpan.FromMilliseconds(180);
        show.RenderTransform = new TranslateTransform(0, 6);
        show.RenderTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(6, 0, time) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        show.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, time));

        if (show == QueueView)
            QueueView.Refresh();
    }

    void StyleFrame()
    {
        handle = new WindowInteropHelper(this).Handle;
        Native.SetDwm(handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_ROUND);
        Native.SetDwm(handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        Native.SetDwm(handle, Native.DWMWA_BORDER_COLOR, 0x00262320);
        UpdateShellPresence();
    }

    // With the tile on, Riffle is a flyout that belongs to the tile and has no taskbar
    // button. With the tile off it needs a normal button, or there'd be no way back.
    void UpdateShellPresence()
    {
        bool flyout = player.ShowTile;
        ShowInTaskbar = !flyout;
        if (handle == IntPtr.Zero)
            return;
        if (flyout)
            Native.AddExStyle(handle, Native.WS_EX_TOOLWINDOW, Native.WS_EX_APPWINDOW);
        else
            Native.AddExStyle(handle, 0, Native.WS_EX_TOOLWINDOW);
    }

    void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Player.ShowTile))
            UpdateShellPresence();
    }

    // anchor is the tile's rectangle in screen pixels, if it's showing
    internal void ShowFlyout(Native.RECT? anchor = null)
    {
        var area = SystemParameters.WorkArea;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double right = anchor is { } tile ? Math.Min(area.Right - 8, tile.Right / scale + 6) : area.Right - 12;
        Left = right - Width;
        Top = area.Bottom - Height - 12;

        Show();
        Activate();
        if (handle != IntPtr.Zero)
            Native.SetForegroundWindow(handle);

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
    }

    internal void ToggleFlyout(Native.RECT? anchor = null)
    {
        // The click on the tile that lands right after the flyout lost focus to it
        // would otherwise open it straight back up.
        if (IsVisible)
            HideFlyout(force: true);
        else if (Environment.TickCount64 - hiddenAt > 250)
            ShowFlyout(anchor);
    }

    void HideFlyout(bool force = false)
    {
        if (!IsVisible || quitting || (holdOpen && !force))
            return;
        hiddenAt = Environment.TickCount64;
        Hide();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox)
        {
            if (e.Key == Key.Escape)
                HideFlyout();
            return;
        }

        switch (e.Key)
        {
            case Key.Space: player.Toggle(); break;
            case Key.Right: player.Next(); break;
            case Key.Left: player.Previous(); break;
            case Key.Up: player.Volume += 0.05; break;
            case Key.Down: player.Volume -= 0.05; break;
            case Key.Escape: HideFlyout(); break;
            default: return;
        }
        e.Handled = true;
    }

    public void CloseForGood()
    {
        quitting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // closing only tucks Riffle away, quitting is in Settings and the tile's menu
        if (quitting)
            return;
        e.Cancel = true;
        HideFlyout();
    }
}
