using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Riffle.Playback;
using Riffle.Shell;
using Riffle.Themes;

namespace Riffle.Views;

// What's playing, in a corner of the screen the game is on. It can't be clicked and never
// takes focus, so it's never in the way. Exclusive fullscreen games draw over everything,
// it shows in borderless and windowed ones.
public partial class OverlayWindow : Window
{
    const int WS_EX_TRANSPARENT = 0x20;
    static readonly TimeSpan Hold = TimeSpan.FromSeconds(2);

    readonly Player player;
    readonly DispatcherTimer holdTimer = new();
    readonly DispatcherTimer changeTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    IntPtr handle;
    bool showing;

    public OverlayWindow(Player player)
    {
        this.player = player;
        InitializeComponent();
        holdTimer.Tick += (_, _) => { holdTimer.Stop(); if (!player.OverlayAlways) FadeOut(); };
        // a song change shows once its cover has had a moment to arrive
        changeTimer.Tick += (_, _) => { changeTimer.Stop(); ShowBriefly(); };

        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(handle, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);
        };

        player.PropertyChanged += OnPlayerChanged;
        player.ArtChanged += Fill;
        player.TrackChanged += () =>
        {
            Fill();
            if (player.OverlayOnChange && !player.OverlayAlways)
            {
                changeTimer.Stop();
                changeTimer.Start();
            }
        };
        Fill();
        Dispatcher.BeginInvoke(KeepUp, DispatcherPriority.ApplicationIdle);
    }

    void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Player.OverlayAlways):
            case nameof(Player.OverlayCorner):
                KeepUp();
                break;
            case nameof(Player.IsPlaying):
            case nameof(Player.UltraPerformance):
                Fill();
                break;
        }
    }

    void Fill()
    {
        TitleText.Text = player.Title;
        ArtistText.Text = player.IsPlaying ? player.Artist : "Paused  ·  " + player.Artist;
        Mark.Visibility = player.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        if (Theme.Still || player.Art.Thumb is not { } thumb)
        {
            Cover.ClearValue(BackgroundProperty);
            return;
        }
        var picture = new ImageBrush(thumb) { Stretch = Stretch.UniformToFill };
        picture.Freeze();
        Cover.Background = picture;
    }

    void KeepUp()
    {
        if (player.OverlayAlways && player.HasTrack)
            Appear();
        else if (showing && !holdTimer.IsEnabled)
            FadeOut();
    }

    public void ShowBriefly()
    {
        if (!player.HasTrack)
            return;
        Fill();
        Appear();
        holdTimer.Stop();
        holdTimer.Interval = Hold + TimeSpan.FromMilliseconds(Theme.Still ? 0 : 200);
        holdTimer.Start();
    }

    void Appear()
    {
        if (!IsVisible)
            Show();
        UpdateLayout();
        Place();
        if (!showing)
        {
            showing = true;
            BeginAnimation(OpacityProperty, Theme.Still ? null : new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
            if (Theme.Still)
                Opacity = 1;
        }
    }

    void FadeOut()
    {
        showing = false;
        if (Theme.Still)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            Hide();
            return;
        }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(450));
        fade.Completed += (_, _) => { if (!showing) Hide(); };
        BeginAnimation(OpacityProperty, fade);
    }

    // On the screen of whatever has focus, the game most of the time.
    void Place()
    {
        if (handle == IntPtr.Zero)
            return;
        var monitor = Native.MonitorFromWindow(Native.GetForegroundWindow(), Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { Size = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(monitor, ref info))
            return;
        Native.GetWindowRect(handle, out var self);
        int width = self.Right - self.Left, height = self.Bottom - self.Top;
        int gap = (int)(14 * Native.GetDpiForWindow(handle) / 96.0);
        var screen = info.Monitor;
        string corner = player.OverlayCorner;
        int x = corner.EndsWith("Left", StringComparison.Ordinal) ? screen.Left + gap : screen.Right - width - gap;
        int y = corner.StartsWith("Bottom", StringComparison.Ordinal) ? screen.Bottom - height - gap * 4 : screen.Top + gap;
        Native.SetWindowPos(handle, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }
}
