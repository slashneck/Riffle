namespace Riffle.Services;

// Launching Riffle again just brings the running one forward.
sealed class SingleInstance : IDisposable
{
    const string MutexName = @"Local\Riffle.Player.Instance";
    const string SignalName = @"Local\Riffle.Player.Show";

    readonly Mutex mutex;
    readonly EventWaitHandle signal;
    readonly RegisteredWaitHandle registration;

    SingleInstance(Mutex mutex, EventWaitHandle signal, Action onSignal)
    {
        this.mutex = mutex;
        this.signal = signal;
        registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => onSignal(), null, -1, false);
    }

    public static SingleInstance? TryClaim(Action onSignal)
    {
        var mutex = new Mutex(true, MutexName, out bool created);
        if (!created)
        {
            mutex.Dispose();
            if (EventWaitHandle.TryOpenExisting(SignalName, out var existing))
            {
                existing.Set();
                existing.Dispose();
            }
            return null;
        }

        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        return new SingleInstance(mutex, signal, onSignal);
    }

    public void Dispose()
    {
        registration.Unregister(null);
        signal.Dispose();
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
