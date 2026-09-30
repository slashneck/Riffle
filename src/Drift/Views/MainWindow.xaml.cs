using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Drift.Radio;
using Drift.Services;
using Drift.Shell;
using Drift.Themes;

namespace Drift.Views;

public partial class MainWindow : Window
{
    static readonly (string Name, string Url)[] Supporters =
    [
        ("SomaFM", "https://somafm.com/support/"),
        ("NTS", "https://www.nts.live/supporters"),
        ("Radio Paradise", "https://radioparadise.com/support"),
        ("Chillhop Music", "https://chillhop.com"),
        ("Lofi Girl", "https://lofigirl.com"),
    ];

    static readonly (string Label, int Minutes)[] SleepChoices =
    [
        ("Off", 0), ("15 minutes", 15), ("30 minutes", 30), ("45 minutes", 45), ("1 hour", 60), ("2 hours", 120),
    ];

    readonly Player player;
    IntPtr handle;
    bool showingA = true;
    bool quitting;
    bool settingsOpen;
    bool syncing;
    long hiddenAt;

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
        UpdateTransport();
        UpdateSleep();
        FillSettings();

        SupportList.ItemsSource = Supporters.Select(s => new { s.Name, s.Url }).ToList();
        VersionText.Text = $"Drift {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";

        Loaded += (_, _) => SyncLists();
        SourceInitialized += (_, _) => StyleFrame();
        Deactivated += (_, _) => HideFlyout();
        PreviewKeyDown += OnKey;
    }

    public event Action? QuitRequested;

    void StyleFrame()
    {
        handle = new WindowInteropHelper(this).Handle;
        Native.SetDwm(handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_ROUND);
        Native.SetDwm(handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        Native.SetDwm(handle, Native.DWMWA_BORDER_COLOR, 0x00262320);
        UpdateShellPresence();
    }

    // With the tile on, Drift is a flyout that belongs to the tile and has no taskbar
    // button. With the tile off it needs a normal button, or there'd be no way back.
    public void UpdateShellPresence()
    {
        bool flyout = player.ShowInTaskbar;
        ShowInTaskbar = !flyout;
        if (handle == IntPtr.Zero)
            return;
        if (flyout)
            Native.AddExStyle(handle, Native.WS_EX_TOOLWINDOW, Native.WS_EX_APPWINDOW);
        else
            Native.AddExStyle(handle, 0, Native.WS_EX_TOOLWINDOW);
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
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        if (handle != IntPtr.Zero)
            Native.SetForegroundWindow(handle);
    }

    internal void ToggleFlyout(Native.RECT? anchor = null)
    {
        // The click on the tile that lands right after the flyout lost focus to it
        // would otherwise open it straight back up.
        if (IsVisible)
            HideFlyout();
        else if (Environment.TickCount64 - hiddenAt > 250)
            ShowFlyout(anchor);
    }

    void HideFlyout()
    {
        if (!IsVisible || quitting)
            return;
        hiddenAt = Environment.TickCount64;
        Hide();
        if (settingsOpen)
            ShowPage(settings: false, animate: false);
    }

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
            case nameof(Player.Mood):
                SyncLists();
                UpdateTransport();
                break;
            case nameof(Player.PlaysSongs):
            case nameof(Player.CanBlock):
                UpdateTransport();
                break;
            case nameof(Player.IsSleeping):
            case nameof(Player.SleepLabel):
                UpdateSleep();
                break;
            case nameof(Player.CustomPlaylists):
            case nameof(Player.BlockedCount):
                FillSettings();
                break;
            case nameof(Player.ShowInTaskbar):
                UpdateShellPresence();
                break;
        }
    }

    // Selection is pushed after the lists have their new items instead of going through
    // a binding, which loses the race when the list is swapped.
    void SyncLists() => Dispatcher.BeginInvoke(() =>
    {
        syncing = true;
        MoodTabs.SelectedItem = player.Mood;
        StationChips.SelectedItem = player.Station;
        syncing = false;
    }, DispatcherPriority.Loaded);

    // keyboard and screen readers change the selection without a click
    void OnMoodPicked(object sender, SelectionChangedEventArgs e)
    {
        if (!syncing && MoodTabs.SelectedItem is Mood mood && mood != player.Mood)
            player.SelectMood(mood);
    }

    void OnStationPicked(object sender, SelectionChangedEventArgs e)
    {
        if (!syncing && StationChips.SelectedItem is Station station && station != player.Station)
            player.Select(station);
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

    void UpdateTransport()
    {
        bool songs = player.PlaysSongs;
        PreviousButton.ToolTip = songs ? "Previous song" : "Previous station";
        NextButton.ToolTip = songs ? "Next song" : "Next station";
        BlockButton.Visibility = songs ? Visibility.Visible : Visibility.Hidden;
        BlockButton.IsEnabled = player.CanBlock;
        BlockButton.Opacity = player.CanBlock ? 1 : 0.35;
    }

    void UpdateSleep()
    {
        SleepButton.ToolTip = player.SleepLabel;
        SleepButton.Foreground = player.IsSleeping ? Theme.Accent : (Brush)FindResource("SubtleTextBrush");
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

    void ShowPage(bool settings, bool animate = true)
    {
        settingsOpen = settings;
        var (show, hide) = settings ? ((FrameworkElement)SettingsPage, (FrameworkElement)NowPage) : (NowPage, SettingsPage);
        var time = TimeSpan.FromMilliseconds(animate ? 220 : 0);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        show.Visibility = Visibility.Visible;
        show.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, time) { EasingFunction = ease });
        if (show.RenderTransform is TranslateTransform slide)
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(settings ? 24 : -24, 0, time) { EasingFunction = ease });

        var fade = new DoubleAnimation(0, time);
        fade.Completed += (_, _) =>
        {
            if (settingsOpen == settings)
                hide.Visibility = Visibility.Collapsed;
        };
        hide.BeginAnimation(OpacityProperty, fade);
    }

    void FillSettings()
    {
        var fade = player.Crossfade switch { "short" => FadeShort, "long" => FadeLong, _ => FadeNormal };
        fade.IsChecked = true;
        (player.FollowCover ? ColorsCover : ColorsCalm).IsChecked = true;

        PlaylistList.ItemsSource = null;
        PlaylistList.ItemsSource = player.CustomPlaylists;

        int blocked = player.BlockedCount;
        BlockedText.Text = blocked switch
        {
            0 => "Songs you skip with the thumbs down show up here.",
            1 => "1 song won't play again.",
            _ => $"{blocked} songs won't play again.",
        };
        ClearBlockedButton.Visibility = blocked > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnHeaderDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    void OnToggle(object sender, RoutedEventArgs e) => player.Toggle();
    void OnNext(object sender, RoutedEventArgs e) => player.Next();
    void OnPrevious(object sender, RoutedEventArgs e) => player.Previous();
    void OnBlock(object sender, RoutedEventArgs e) => player.BlockCurrent();
    void OnClose(object sender, RoutedEventArgs e) => HideFlyout();
    void OnOpenSettings(object sender, RoutedEventArgs e) => ShowPage(settings: true);
    void OnCloseSettings(object sender, RoutedEventArgs e) => ShowPage(settings: false);
    void OnClearBlocked(object sender, RoutedEventArgs e) => player.ClearBlocked();
    void OnQuit(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();

    void OnMoodClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: Mood mood })
            player.SelectMood(mood);
    }

    // clicking the chip that's already selected should still start it
    void OnChipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: Station station })
            player.Select(station);
    }

    void OnSleep(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = SleepButton,
            Placement = PlacementMode.Top,
            HorizontalOffset = -120,
        };
        Theme.Install(menu.Resources);

        if (player.IsSleeping)
            menu.Items.Add(new MenuItem { Header = player.SleepLabel, IsEnabled = false });

        foreach (var (label, minutes) in SleepChoices)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = minutes == 0 && !player.IsSleeping };
            item.Click += (_, _) => player.SleepIn(minutes == 0 ? null : TimeSpan.FromMinutes(minutes));
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    void OnCrossfade(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string value } && value != player.Crossfade)
            player.Crossfade = value;
    }

    void OnColors(object sender, RoutedEventArgs e)
    {
        bool cover = sender == ColorsCover;
        if (cover != player.FollowCover)
            player.FollowCover = cover;
    }

    async void OnAddPlaylist(object sender, RoutedEventArgs e)
    {
        var link = PlaylistLink.Text;
        if (string.IsNullOrWhiteSpace(link))
            return;

        AddButton.IsEnabled = false;
        PlaylistNote.Visibility = Visibility.Visible;
        PlaylistNote.Text = "Checking the playlist";
        var problem = await player.AddPlaylistAsync(link, null);
        AddButton.IsEnabled = true;

        if (problem is null)
        {
            PlaylistLink.Text = "";
            PlaylistNote.Text = "Added. You'll find it under Yours.";
        }
        else
        {
            PlaylistNote.Text = problem;
        }
    }

    void OnPlaylistKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnAddPlaylist(sender, e);
    }

    void OnRemovePlaylist(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CustomPlaylist playlist })
            player.RemovePlaylist(playlist);
    }

    void OnSupport(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url } && url.StartsWith("https://", StringComparison.Ordinal))
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception) { }
        }
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        // typing a playlist link shouldn't pause the music
        if (e.OriginalSource is TextBox)
        {
            if (e.Key == Key.Escape)
                ShowPage(settings: false);
            return;
        }

        switch (e.Key)
        {
            case Key.Space: player.Toggle(); break;
            case Key.Right: player.Next(); break;
            case Key.Left: player.Previous(); break;
            case Key.Up: player.Volume += 0.05; break;
            case Key.Down: player.Volume -= 0.05; break;
            case Key.Escape when settingsOpen: ShowPage(settings: false); break;
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
        // closing only tucks Drift away, quitting is in the tile's menu
        if (quitting)
            return;
        e.Cancel = true;
        HideFlyout();
    }
}
