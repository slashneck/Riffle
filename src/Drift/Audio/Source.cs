using Drift.Radio;

namespace Drift.Audio;

public enum SourceState { Connecting, Buffering, Playing, Failed }

// What's playing right now, whichever kind of source it came from.
public sealed record TrackInfo(string? Artist, string? Title, string? ArtUrl, bool IsAdvert = false, string? VideoId = null)
{
    public bool HasTrack => !string.IsNullOrWhiteSpace(Title);
}

// Something the mixer can play: a radio stream, a YouTube playlist or generated noise.
// Subclasses only produce samples; the fade envelope, readiness and events live here.
public abstract class Source : IDisposable
{
    protected const int SampleRate = 44100;

    sealed record FadeRequest(float Target, double Seconds);

    readonly SynchronizationContext ui;
    protected readonly CancellationTokenSource cancel = new();
    volatile FadeRequest? pendingFade;

    // audio thread only
    float[] scratch = new float[8192];
    float amount;
    float target;
    float step = 1f;
    float duck = 1f;
    bool fadingOut;
    bool started;

    protected Source(Station station, SynchronizationContext ui)
    {
        Station = station;
        this.ui = ui;
    }

    public Station Station { get; }
    public SourceState State { get; private set; } = SourceState.Connecting;
    public TrackInfo? Track { get; private set; }
    public bool HasStarted { get; private set; }
    public bool Retired { get; set; }
    public virtual bool CanSkip => false;

    // true once a fade out has fully landed on silence
    public bool IsSilent => fadingOut && amount == 0f && pendingFade is null;

    public event Action<Source>? Ready;
    public event Action<Source>? StateChanged;
    public event Action<Source>? TrackChanged;

    public abstract void Start();

    public virtual void Skip() { }

    public virtual void Back() { }

    public void FadeTo(float level, double seconds) => pendingFade = new FadeRequest(level, seconds);

    // Fill `buffer` with `count` interleaved stereo samples. Return false when there's
    // nothing to play yet (still loading, or buffering after a hiccup).
    protected abstract bool Render(float[] buffer, int count);

    public void MixInto(float[] dest, int offset, int count)
    {
        if (pendingFade is { } fade)
        {
            pendingFade = null;
            target = fade.Target;
            fadingOut = fade.Target == 0f;
            step = fade.Seconds <= 0 ? 1f : (float)(1.0 / (fade.Seconds * SampleRate));
        }

        int frames = count / 2;
        if (scratch.Length < count)
            scratch = new float[count];

        if (!Render(scratch, count))
        {
            for (int i = 0; i < frames && amount != target; i++)
                Tick();
            return;
        }

        if (!started)
        {
            started = true;
            Post(() =>
            {
                HasStarted = true;
                SetState(SourceState.Playing);
                Ready?.Invoke(this);
            });
        }

        const float duckStep = 1f / (0.35f * SampleRate);
        for (int i = 0; i < frames; i++)
        {
            Tick();
            if (duck < 1f)
                duck = MathF.Min(1f, duck + duckStep);

            float gain = MathF.Sin(amount * MathF.PI * 0.5f) * duck;
            dest[offset + i * 2] += scratch[i * 2] * gain;
            dest[offset + i * 2 + 1] += scratch[i * 2 + 1] * gain;
        }
    }

    void Tick()
    {
        if (amount < target)
            amount = MathF.Min(target, amount + step);
        else if (amount > target)
            amount = MathF.Max(target, amount - step);
    }

    // eases back in after a gap instead of snapping to full volume
    protected void DuckIn() => duck = 0f;

    protected void SetState(SourceState state)
    {
        if (State == state || cancel.IsCancellationRequested)
            return;
        State = state;
        StateChanged?.Invoke(this);
    }

    protected void SetTrack(TrackInfo? track)
    {
        if (cancel.IsCancellationRequested)
            return;
        Track = track;
        TrackChanged?.Invoke(this);
    }

    protected void Post(Action action) => ui.Post(_ =>
    {
        if (!cancel.IsCancellationRequested)
            action();
    }, null);

    public virtual void Dispose() => cancel.Cancel();
}
