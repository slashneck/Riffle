using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Riffle.Services;

public enum UpdateState { Idle, Checking, Latest, Available, Downloading, Ready, Failed }

// Asks GitHub for a new version every few hours and downloads it in the background. It
// installs by itself only when the music is stopped and nobody's at the PC, so a song
// never gets cut off. Settings has a button for doing it right away.
public sealed class UpdateService
{
    static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);
    static readonly TimeSpan IdleBeforeInstall = TimeSpan.FromMinutes(10);

    readonly Func<bool> enabled;
    readonly Func<bool> busy;
    readonly Action exitForInstall;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(45) };
    DateTime lastCheck = DateTime.MinValue;
    UpdateInfo? available;
    string? staged;
    bool running;

    public UpdateService(Func<bool> enabled, Func<bool> busy, Action exitForInstall)
    {
        this.enabled = enabled;
        this.busy = busy;
        this.exitForInstall = exitForInstall;
        timer.Tick += OnTick;
        timer.Start();
    }

    public event Action? Changed;

    public UpdateState State { get; private set; }
    public double Progress { get; private set; }
    public string? Error { get; private set; }
    public string? Version => available?.Version;
    public string PageUrl => available?.PageUrl ?? Updater.RepoUrl + "/releases";

    async void OnTick(object? sender, EventArgs e)
    {
        timer.Interval = TimeSpan.FromMinutes(10);
        if (staged is not null)
        {
            if (!busy() && IdleTime() >= IdleBeforeInstall)
                Install();
            return;
        }
        if (enabled() && DateTime.UtcNow - lastCheck >= CheckEvery)
            await CheckAsync();
    }

    public async Task CheckAsync()
    {
        if (running || staged is not null)
            return;
        running = true;
        try
        {
            Set(UpdateState.Checking);
            lastCheck = DateTime.UtcNow;
            available = await Updater.CheckAsync();
            if (available is null)
            {
                Set(UpdateState.Latest);
                return;
            }
            if (!Updater.CanSelfUpdate)
            {
                Set(UpdateState.Available);
                return;
            }

            Progress = 0;
            Set(UpdateState.Downloading);
            var report = new Progress<double>(p =>
            {
                if (p - Progress < 0.02 && p < 1)
                    return;
                Progress = p;
                Changed?.Invoke();
            });
            staged = await Updater.DownloadAsync(available, ((IProgress<double>)report).Report);
            Set(UpdateState.Ready);
        }
        catch (Exception error)
        {
            Error = error is HttpRequestException or TaskCanceledException ? "GitHub couldn't be reached" : error.Message;
            Set(UpdateState.Failed);
        }
        finally
        {
            running = false;
        }
    }

    public void Install()
    {
        if (staged is null)
            return;
        Updater.StartInstall(staged);
        exitForInstall();
    }

    void Set(UpdateState state)
    {
        State = state;
        if (state != UpdateState.Failed)
            Error = null;
        Changed?.Invoke();
    }

    static TimeSpan IdleTime()
    {
        var info = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : TimeSpan.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LastInput
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LastInput info);
}
