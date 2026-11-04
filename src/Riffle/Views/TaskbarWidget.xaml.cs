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

public partial class TaskbarWidget : Window
{
    const double FullWidth = 228;
    const double MinTextWidth = 150;
    const double CompactWidth = 46;

    static readonly Brush DarkTitle = Frozen(Color.FromRgb(0xF3, 0xF4, 0xF6));
    static readonly Brush DarkSub = Frozen(Color.FromRgb(0xA9, 0xAF, 0xB9));
    static readonly Brush DarkHover = Frozen(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
    static readonly Brush LightTitle = Frozen(Color.FromRgb(0x1A, 0x1B, 0x1F));
    static readonly Brush LightSub = Frozen(Color.FromRgb(0x5A, 0x61, 0x6C));
    static readonly Brush LightHover = Frozen(Color.FromArgb(0x10, 0x00, 0x00, 0x00));
    static readonly Brush PausedMark = Frozen(Color.FromRgb(0x8A, 0x90, 0x9A));

    readonly Player player;
    readonly MainWindow main;
    readonly TaskbarTracker tracker = new();
    IntPtr handle;
    bool showingA = true;
    bool light;
    string? volumeNote;
    CancellationTokenSource? volumeTimer;

    public TaskbarWidget(Player player, MainWindow main)
    {
        this.player = player;
        this.main = main;
        InitializeComponent();

        ApplyTaskbarTheme(false);
        ShowArt(animate: false);
        UpdateText();
        UpdatePlayState();

        player.PropertyChanged += OnPlayerChanged;
        player.ArtChanged += () => ShowArt(animate: true);
        Theme.StaticPropertyChanged += (_, e) => { if (e.PropertyName == nameof(Theme.Accent)) PaintIndicator(); };
        tracker.SlotChanged += Place;

        Root.MouseEnter += (_, _) =>
        {
            Fade(HoverPlate, 1, 120);
            if (TextPanel.Visibility == Visibility.Visible)
            {
                Bars.Visibility = Visibility.Collapsed;
                Controls.Visibility = Visibility.Visible;
            }
        };
        Root.MouseLeave += (_, _) =>
        {
            Fade(HoverPlate, 0, 200);
            if (TextPanel.Visibility == Visibility.Visible)
                Bars.Visibility = Visibility.Visible;
            Controls.Visibility = Visibility.Collapsed;
        };
        Root.MouseLeftButtonUp += (_, _) => main.ToggleFlyout(Bounds);
        Root.MouseUp += OnMouseUp;
        Root.MouseWheel += OnWheel;
        Root.ContextMenuOpening += (_, _) => Root.ContextMenu = BuildMenu();
        Root.ContextMenu = BuildMenu();
    }

    public event Action? QuitRequested;

    internal Native.RECT? Bounds =>
        IsVisible && handle != IntPtr.Zero && Native.GetWindowRect(handle, out var rect) ? rect : null;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        handle = new WindowInteropHelper(this).Handle;
        Native.AddExStyle(handle, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE, Native.WS_EX_APPWINDOW);

        // Transparency through the window manager rather than a layered window: the GPU
        // composites it, so animating at full frame rate costs next to nothing.
        HwndSource.FromHwnd(handle)!.CompositionTarget.BackgroundColor = Colors.Transparent;
        var everything = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(handle, ref everything);
        Native.SetDwm(handle, Native.DWMWA_NCRENDERING_POLICY, Native.DWMNCRP_DISABLED);
        Native.SetDwm(handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_DONOTROUND);
        Native.SetDwm(handle, Native.DWMWA_BORDER_COLOR, Native.DWMWA_COLOR_NONE);
        tracker.Attach(handle);
    }

    public void Start()
    {
        // create the window without showing it, the tracker decides when it appears
        new WindowInteropHelper(this).EnsureHandle();
        tracker.Enabled = player.ShowTile;
        Place();
    }

    void Place()
    {
        tracker.Enabled = player.ShowTile;
        var slot = tracker.Slot;
        if (light != slot.LightTheme)
            ApplyTaskbarTheme(slot.LightTheme);

        double scale = slot.Dpi / 96.0;
        int right = slot.Right - (int)(10 * scale);
        int available = right - slot.Left - (int)(12 * scale);

        bool wanted = player.ShowTile && slot.Visible;
        bool compact = available < MinTextWidth * scale;
        if (!wanted || available < CompactWidth * scale)
        {
            if (IsVisible)
                Hide();
            return;
        }

        int width = compact ? (int)(CompactWidth * scale) : (int)Math.Min(FullWidth * scale, available);
        TextPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SideSlot.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;

        if (!IsVisible)
            Show();

        Native.SetWindowPos(handle, Native.HWND_TOPMOST, right - width, slot.Top, width, slot.Height,
            Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    void ApplyTaskbarTheme(bool isLight)
    {
        light = isLight;
        TitleText.Foreground = isLight ? LightTitle : DarkTitle;
        SubText.Foreground = isLight ? LightSub : DarkSub;
        HoverPlate.Background = isLight ? LightHover : DarkHover;
        TileToggle.Foreground = TileNext.Foreground = isLight ? LightTitle : DarkTitle;
    }

    void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Player.Title):
            case nameof(Player.Artist):
                UpdateText();
                break;
            case nameof(Player.IsPlaying):
            case nameof(Player.UltraPerformance):
                UpdatePlayState();
                break;
            case nameof(Player.IsLoadingSlowly):
                UpdateText();
                TileToggle.IsEnabled = !player.IsLoadingSlowly;
                break;
            case nameof(Player.ShowTile):
                Place();
                break;
        }
    }

    void UpdateText()
    {
        TitleText.Text = player.Title;
        SubText.Text = volumeNote ?? (player.IsLoadingSlowly ? "Loading song" : player.Artist);
    }

    void UpdatePlayState()
    {
        Bars.Active = player.IsPlaying && !Theme.Still;
        TileToggle.Content = player.IsPlaying ? "" : "";
        PaintIndicator();
        if (Theme.Still)
        {
            Indicator.BeginAnimation(WidthProperty, null);
            Indicator.Width = player.IsPlaying ? 16 : 6;
        }
        else
        {
            Indicator.BeginAnimation(WidthProperty, new DoubleAnimation(player.IsPlaying ? 16 : 6, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
        Indicator.Margin = new Thickness(player.IsPlaying ? 15 : 20, 0, 0, 2);
    }

    void PaintIndicator() => Indicator.Background = player.IsPlaying ? Theme.Accent : PausedMark;

    void ShowArt(bool animate)
    {
        var (nextBorder, oldBorder) = showingA ? (CoverB, CoverA) : (CoverA, CoverB);
        if (Theme.Still)
        {
            nextBorder.BeginAnimation(OpacityProperty, null);
            oldBorder.BeginAnimation(OpacityProperty, null);
            CoverA.Background = CoverB.Background = null;
            return;
        }

        var picture = new ImageBrush(player.Art.Thumb) { Stretch = Stretch.UniformToFill };
        picture.Freeze();
        nextBorder.Background = picture;
        showingA = !showingA;

        var time = animate ? TimeSpan.FromMilliseconds(700) : TimeSpan.Zero;
        Panel.SetZIndex(nextBorder, 1);
        Panel.SetZIndex(oldBorder, 0);
        nextBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, time));
        oldBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, time) { BeginTime = time });
    }

    // The tile never takes focus, and WPF buttons need focus to count a click, so these
    // listen for the mouse directly.
    void OnButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    void OnToggle(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        player.Toggle();
    }

    void OnNext(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        player.Next();
    }

    void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
            player.Toggle();
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        player.Volume += e.Delta > 0 ? 0.04 : -0.04;

        volumeTimer?.Cancel();
        var cts = volumeTimer = new CancellationTokenSource();
        volumeNote = $"Volume {Math.Round(player.Volume * 100)}%";
        UpdateText();
        Task.Delay(1400, cts.Token).ContinueWith(_ =>
        {
            volumeNote = null;
            UpdateText();
        }, cts.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        var toggle = new MenuItem { Header = player.IsPlaying ? "Pause" : "Play", Tag = player.IsPlaying ? "" : "" };
        toggle.Click += (_, _) => player.Toggle();

        var next = new MenuItem { Header = "Next", Tag = "" };
        next.Click += (_, _) => player.Next();

        var reshuffle = new MenuItem { Header = "Reshuffle what's left", Tag = "" };
        reshuffle.Click += (_, _) => player.Reshuffle();

        var never = new MenuItem { Header = "Never play this again", Tag = "" };
        never.Click += (_, _) => player.NeverPlayCurrent();

        var open = new MenuItem { Header = "Open Riffle", Tag = "" };
        open.Click += (_, _) => main.ShowFlyout(Bounds);

        var quit = new MenuItem { Header = "Quit Riffle", Tag = "" };
        quit.Click += (_, _) => QuitRequested?.Invoke();

        menu.Items.Add(toggle);
        menu.Items.Add(next);
        menu.Items.Add(reshuffle);
        menu.Items.Add(never);
        menu.Items.Add(new Separator());
        menu.Items.Add(open);
        menu.Items.Add(quit);
        return menu;
    }

    static void Fade(UIElement element, double to, int ms) =>
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, Theme.Still ? TimeSpan.Zero : TimeSpan.FromMilliseconds(ms)));

    static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    protected override void OnClosed(EventArgs e)
    {
        tracker.Dispose();
        base.OnClosed(e);
    }
}
