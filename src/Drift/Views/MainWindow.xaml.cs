using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Drift.Shell;
using Drift.Themes;

namespace Drift.Views;

public partial class MainWindow : Window
{
    readonly Player player;
    bool showingA = true;
    bool placedByUser;
    bool quitting;

    public MainWindow(Player player)
    {
        this.player = player;
        Theme.Install(Resources);
        InitializeComponent();
        DataContext = player;

        player.PropertyChanged += OnPlayerChanged;
        player.ArtChanged += () => ShowArt(animate: true);
        ShowArt(animate: false);
        UpdatePlayState();
        UpdateVolumeIcon();
        Loaded += (_, _) => SyncStationChips();

        SourceInitialized += (_, _) => StyleFrame();
        LocationChanged += (_, _) => { if (IsLoaded && IsVisible) placedByUser = true; };
        PreviewKeyDown += OnKey;
    }

    public event Action? QuitRequested;

    void StyleFrame()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.SetDwm(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_ROUND);
        Native.SetDwm(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        Native.SetDwm(hwnd, Native.DWMWA_BORDER_COLOR, 0x00262320);
    }

    public void ShowFlyout()
    {
        if (!placedByUser)
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 12;
            Top = area.Bottom - Height - 12;
        }

        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        placedByUser = placedByUser && IsOnScreen();
    }

    public void ToggleFlyout()
    {
        if (IsVisible && IsActive)
            Hide();
        else
            ShowFlyout();
    }

    bool IsOnScreen() =>
        Left >= SystemParameters.VirtualScreenLeft - 20 &&
        Top >= SystemParameters.VirtualScreenTop - 20 &&
        Left + Width <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 20 &&
        Top + Height <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight + 20;

    void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Player.IsPlaying):
            case nameof(Player.IsBusy):
                UpdatePlayState();
                break;
            case nameof(Player.Volume):
                UpdateVolumeIcon();
                break;
            case nameof(Player.Station):
            case nameof(Player.Stations):
                SyncStationChips();
                break;
        }
    }

    void UpdatePlayState()
    {
        PlayButton.Content = player.IsPlaying ? "" : "";
        PlayButton.ToolTip = player.IsPlaying ? "Pause" : "Play";
        AutomationProperties.SetName(PlayButton, player.IsPlaying ? "Pause" : "Play");

        if (player.IsPlaying && player.IsBusy)
        {
            var pulse = new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(800))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase(),
            };
            BusyDot.BeginAnimation(OpacityProperty, pulse);
        }
        else
        {
            BusyDot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
        }
    }

    void UpdateVolumeIcon()
    {
        double v = player.Volume;
        VolumeIcon.Text = v <= 0.001 ? "" : v < 0.34 ? "" : v < 0.67 ? "" : "";
    }

    void ShowArt(bool animate)
    {
        var art = player.Art;
        var (nextBrush, nextBorder, oldBorder) = showingA
            ? (CoverBrushB, CoverB, CoverA)
            : (CoverBrushA, CoverA, CoverB);
        var (nextBackdrop, oldBackdrop) = showingA ? (BackdropB, BackdropA) : (BackdropA, BackdropB);

        nextBrush.ImageSource = art.Image;
        nextBackdrop.Source = art.Thumb;
        showingA = !showingA;

        var time = animate ? TimeSpan.FromMilliseconds(900) : TimeSpan.Zero;
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        Panel.SetZIndex(nextBorder, 1);
        Panel.SetZIndex(oldBorder, 0);
        nextBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, time) { EasingFunction = ease });
        oldBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, time) { BeginTime = time / 2 });
        nextBackdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(1, time * 1.6) { EasingFunction = ease });
        oldBackdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, time * 1.6) { EasingFunction = ease });
    }

    void OnHeaderDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    void OnToggle(object sender, RoutedEventArgs e) => player.Toggle();
    void OnNext(object sender, RoutedEventArgs e) => player.Next();
    void OnPrevious(object sender, RoutedEventArgs e) => player.Previous();
    void OnClose(object sender, RoutedEventArgs e) => Hide();

    // The chip list is swapped whenever the mood changes, so selection is pushed after
    // the new items exist rather than through a binding that can lose the race.
    void SyncStationChips() =>
        Dispatcher.BeginInvoke(() => StationChips.SelectedItem = player.Station,
            System.Windows.Threading.DispatcherPriority.Loaded);

    void OnStationPicked(object sender, SelectionChangedEventArgs e)
    {
        if (StationChips.SelectedItem is Radio.Station station && station != player.Station)
            player.Select(station);
    }

    // clicking the chip that's already selected should still start it
    void OnChipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: Radio.Station station })
            player.Select(station);
    }

    void OnMore(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = MoreButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            HorizontalOffset = -150,
        };
        Theme.Install(menu.Resources);

        var taskbar = new MenuItem { Header = "Show in taskbar", IsCheckable = true, IsChecked = player.ShowInTaskbar };
        taskbar.Click += (_, _) => player.ShowInTaskbar = taskbar.IsChecked;

        var covers = new MenuItem
        {
            Header = "Find album covers",
            IsCheckable = true,
            IsChecked = player.FindCovers,
            ToolTip = "Looks up cover art for the song that's playing",
        };
        covers.Click += (_, _) => player.FindCovers = covers.IsChecked;

        var station = player.Station;
        var support = new MenuItem { Header = $"Support {station.Source}", Tag = "", IsEnabled = station.Support is not null };
        support.Click += (_, _) => OpenLink(station.Support);

        var quit = new MenuItem { Header = "Quit Drift", Tag = "" };
        quit.Click += (_, _) => QuitRequested?.Invoke();

        menu.Items.Add(taskbar);
        menu.Items.Add(covers);
        menu.Items.Add(new Separator());
        menu.Items.Add(support);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);
        menu.IsOpen = true;
    }

    static void OpenLink(string? url)
    {
        if (url is null || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space: player.Toggle(); break;
            case Key.Right: player.Next(); break;
            case Key.Left: player.Previous(); break;
            case Key.Up: player.Volume += 0.05; break;
            case Key.Down: player.Volume -= 0.05; break;
            case Key.Escape: Hide(); break;
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
        // closing only tucks Drift into the taskbar, quitting is in the menu
        if (quitting)
            return;
        e.Cancel = true;
        Hide();
    }
}
