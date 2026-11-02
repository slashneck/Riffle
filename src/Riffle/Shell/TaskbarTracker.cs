using System.Windows.Automation;
using System.Windows.Threading;
using Microsoft.Win32;
using static Riffle.Shell.Native;

namespace Riffle.Shell;

// Where the free strip of taskbar between the app buttons and the tray is, in physical pixels.
public readonly record struct TaskbarSlot(
    bool Visible,
    int Left,
    int Right,
    int Top,
    int Height,
    uint Dpi,
    bool LightTheme);

// Windows 11 has no API for putting things inside the taskbar, so the widget is a small
// topmost window laid over the empty part of it. This keeps it lined up with the tray,
// clear of the app buttons, above the taskbar in z-order, and hidden whenever the taskbar is.
public sealed class TaskbarTracker : IDisposable
{
    readonly DispatcherTimer timer;
    readonly WinEventProc foregroundProc;
    readonly IntPtr foregroundHook;
    IntPtr widget;
    int contentRight = -1;
    int probedBarWidth = -1;
    int probedTrayLeft = -1;
    long lastProbe;
    bool probing;
    int ticks;
    bool lightTheme;

    public TaskbarTracker()
    {
        lightTheme = ReadLightTheme();
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) => Update();

        foregroundProc = (_, _, _, _, _, _, _) =>
        {
            if (Enabled)
                Update();
        };
        foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    public TaskbarSlot Slot { get; private set; }

    public event Action? SlotChanged;

    public void Attach(IntPtr widgetWindow)
    {
        widget = widgetWindow;
        Update();
    }

    // Nothing is polled while the widget isn't wanted, e.g. before the first song
    // or with the taskbar tile switched off.
    public bool Enabled
    {
        get => timer.IsEnabled;
        set
        {
            if (value == timer.IsEnabled)
                return;
            if (value)
            {
                timer.Start();
                Update();
            }
            else
            {
                timer.Stop();
            }
        }
    }

    public void Update()
    {
        if (widget == IntPtr.Zero)
            return;

        if (++ticks % 12 == 0)
            lightTheme = ReadLightTheme();

        var slot = Measure();
        if (slot != Slot)
        {
            Slot = slot;
            SlotChanged?.Invoke();
        }

        if (slot.Visible)
            KeepAboveTaskbar();
    }

    TaskbarSlot Measure()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !IsWindowVisible(taskbar) || !GetWindowRect(taskbar, out var bar))
            return default;

        var monitor = MonitorFromWindow(taskbar, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { Size = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);

        // vertical taskbars aren't a thing on Windows 11
        if (bar.Width < bar.Height)
            return default;

        int trayLeft = bar.Right;
        var tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray != IntPtr.Zero && GetWindowRect(tray, out var trayRect) && trayRect.Width > 0)
            trayLeft = trayRect.Left;

        if (bar.Width != probedBarWidth || Math.Abs(trayLeft - probedTrayLeft) > 1 ||
            Environment.TickCount64 - lastProbe > 5000)
            ProbeButtons(taskbar, trayLeft, bar.Width);

        int visibleTop = Math.Max(bar.Top, info.Monitor.Top);
        int visibleBottom = Math.Min(bar.Bottom, info.Monitor.Bottom);
        bool slidAway = visibleBottom - visibleTop < bar.Height * 0.6;

        bool visible = !slidAway && !FullscreenAppOn(monitor, info.Monitor);
        uint dpi = GetDpiForWindow(taskbar);
        int left = contentRight > 0 ? contentRight : bar.Left + bar.Width / 2;

        return new TaskbarSlot(visible, left, trayLeft, bar.Top, bar.Height, dpi == 0 ? 96 : dpi, lightTheme);
    }

    // The Win32 rects inside the taskbar are stale leftovers on Windows 11, the real
    // button layout is only exposed through UI Automation. It's slow-ish, so off thread.
    void ProbeButtons(IntPtr taskbar, int trayLeft, int barWidth)
    {
        if (probing)
            return;
        probing = true;
        lastProbe = Environment.TickCount64;
        probedTrayLeft = trayLeft;
        probedBarWidth = barWidth;

        Task.Run(() =>
        {
            int right = -1;
            try
            {
                var root = AutomationElement.FromHandle(taskbar);
                var frame = root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "TaskbarFrame"));
                var items = (frame ?? root).FindAll(TreeScope.Children, Condition.TrueCondition);
                if (items.Count == 0)
                    items = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);

                foreach (AutomationElement item in items)
                {
                    var r = item.Current.BoundingRectangle;
                    if (r.IsEmpty || r.Width <= 0 || r.Right > trayLeft - 1)
                        continue;
                    right = Math.Max(right, (int)Math.Ceiling(r.Right));
                }
            }
            catch (Exception)
            {
            }
            return right;
        }).ContinueWith(t =>
        {
            probing = false;
            if (t.Result != contentRight)
            {
                contentRight = t.Result;
                Update();
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    static bool FullscreenAppOn(IntPtr monitor, RECT screen)
    {
        if (SHQueryUserNotificationState(out int state) == 0 && (state == 3 || state == 4))
            return true;

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST) != monitor)
            return false;

        var name = ClassName(foreground);
        if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;

        return GetWindowRect(foreground, out var r) &&
               r.Left <= screen.Left && r.Top <= screen.Top &&
               r.Right >= screen.Right && r.Bottom >= screen.Bottom;
    }

    void KeepAboveTaskbar()
    {
        if (widget == IntPtr.Zero || !IsWindowVisible(widget))
            return;

        var taskbar = FindWindow("Shell_TrayWnd", null);
        for (var above = GetWindow(widget, GW_HWNDPREV); above != IntPtr.Zero; above = GetWindow(above, GW_HWNDPREV))
        {
            if (above != taskbar)
                continue;
            SetWindowPos(widget, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            return;
        }
    }

    static bool ReadLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
    }

    public void Dispose()
    {
        timer.Stop();
        if (foregroundHook != IntPtr.Zero)
            UnhookWinEvent(foregroundHook);
    }
}
