using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Drift.Shell;
using Drift.Themes;

namespace Drift.Views;

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
        Theme.Install(Resources);
        InitializeComponent();

        ApplyTaskbarTheme(false);
        ShowArt(animate: false);
        UpdateText();
        UpdatePlayState();

        player.PropertyChanged += OnPlayerChanged;
        player.ArtChanged += () => ShowArt(animate: true);
        tracker.SlotChanged += Place;

        Root.MouseEnter += (_, _) => Fade(HoverPlate, 1, 120);
        Root.MouseLeave += (_, _) => Fade(HoverPlate, 0, 200);
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
        tracker.Attach(handle);
    }

    public void Start()
    {
        // create the window without showing it, the tracker decides when it appears
        new WindowInteropHelper(this).EnsureHandle();
        Place();
    }

    void Place()
    {
        var slot = tracker.Slot;
        if (light != slot.LightTheme)
            ApplyTaskbarTheme(slot.LightTheme);

        double scale = slot.Dpi / 96.0;
        int right = slot.Right - (int)(10 * scale);
        int available = right - slot.Left - (int)(12 * scale);

        tracker.Enabled = player.ShowInTaskbar;
        bool wanted = tracker.Enabled && slot.Visible;
        bool compact = available < MinTextWidth * scale;
        if (!wanted || available < CompactWidth * scale)
        {
            if (IsVisible)
                Hide();
            return;
        }

        int width = compact ? (int)(CompactWidth * scale) : (int)Math.Min(FullWidth * scale, available);
        TextPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        Bars.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;

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
    }

    void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Player.Title):
            case nameof(Player.Artist):
            case nameof(Player.Detail):
                UpdateText();
                break;
            case nameof(Player.IsPlaying):
            case nameof(Player.IsBusy):
                UpdatePlayState();
                break;
            case nameof(Player.HasPlayed):
            case nameof(Player.ShowInTaskbar):
                Place();
                break;
        }
    }

    void UpdateText()
    {
        TitleText.Text = player.Title;
        SubText.Text = volumeNote ?? (player.IsBusy ? player.Detail : player.Artist);
    }

    void UpdatePlayState()
    {
        Bars.Active = player.IsPlaying;
        Indicator.Background = player.IsPlaying ? Theme.Accent : PausedMark;
        var width = new DoubleAnimation(player.IsPlaying ? 16 : 6, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Indicator.BeginAnimation(WidthProperty, width);
        Indicator.Margin = new Thickness(player.IsPlaying ? 15 : 20, 0, 0, 2);
        UpdateText();
    }

    void ShowArt(bool animate)
    {
        var art = player.Art;
        var (nextBrush, nextBorder, oldBorder) = showingA
            ? (CoverBrushB, CoverB, CoverA)
            : (CoverBrushA, CoverA, CoverB);

        nextBrush.ImageSource = art.Thumb;
        showingA = !showingA;

        var time = animate ? TimeSpan.FromMilliseconds(700) : TimeSpan.Zero;
        Panel.SetZIndex(nextBorder, 1);
        Panel.SetZIndex(oldBorder, 0);
        nextBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, time));
        oldBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, time) { BeginTime = time });
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
            player.Save();
        }, cts.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        Theme.Install(menu.Resources);

        var toggle = new MenuItem { Header = player.IsPlaying ? "Pause" : "Play", Tag = player.IsPlaying ? "" : "" };
        toggle.Click += (_, _) => player.Toggle();

        var next = new MenuItem { Header = player.PlaysSongs ? "Next song" : "Next station", Tag = "" };
        next.Click += (_, _) => player.Next();

        var open = new MenuItem { Header = "Open Drift", Tag = "" };
        open.Click += (_, _) => main.ShowFlyout(Bounds);

        var hide = new MenuItem { Header = "Hide from taskbar", Tag = "" };
        hide.Click += (_, _) => player.ShowInTaskbar = false;

        var quit = new MenuItem { Header = "Quit Drift", Tag = "" };
        quit.Click += (_, _) => QuitRequested?.Invoke();

        menu.Items.Add(toggle);
        menu.Items.Add(next);
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(hide);
        menu.Items.Add(quit);
        return menu;
    }

    static void Fade(UIElement element, double to, int ms) =>
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)));

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
