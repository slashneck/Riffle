using Drift.Radio;
using Drift.Services;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Drift.Audio;

public enum DeckState { Connecting, Buffering, Playing, Failed }

// One live connection to a station. Network and decoding run on their own thread and
// fill a buffer; the mixer pulls from it on the audio thread and applies the fade.
sealed class Deck : IDisposable
{
    const int SampleRate = 44100;
    const double StartBuffer = 2.0;
    const double Rebuffer = 3.0;
    const int StallTimeoutMs = 12000;
    const int MaxConnectAttempts = 3;

    sealed record Pipe(BufferedWaveProvider Buffer, ISampleProvider Samples);
    sealed record FadeRequest(float Target, double Seconds);

    readonly SynchronizationContext ui;
    readonly CancellationTokenSource cancel = new();
    readonly Thread worker;
    readonly Timer watchdog;

    volatile Pipe? pipe;
    volatile FadeRequest? pendingFade;
    volatile HttpResponseMessage? response;
    volatile IcyStream? icy;
    int failures;

    // audio thread only
    float[] scratch = new float[8192];
    float amount;
    float target;
    float step = 1f;
    float duck = 1f;
    bool flowing;
    bool started;
    bool fadingOut;

    public Deck(Station station, SynchronizationContext ui)
    {
        Station = station;
        this.ui = ui;
        worker = new Thread(Run) { IsBackground = true, Name = "Drift stream" };
        watchdog = new Timer(CheckStall, null, 2000, 2000);
    }

    public Station Station { get; }
    public DeckState State { get; private set; } = DeckState.Connecting;
    public IcyMetadata? Metadata { get; private set; }
    public bool HasStarted { get; private set; }
    public bool Retired { get; set; }

    // true once a fade out has fully landed on silence
    public bool IsSilent => fadingOut && amount == 0f && pendingFade is null;

    public event Action<Deck>? Ready;
    public event Action<Deck>? StateChanged;
    public event Action<Deck>? MetadataChanged;

    public void Start() => worker.Start();

    public void FadeTo(float level, double seconds) => pendingFade = new FadeRequest(level, seconds);

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
        var current = pipe;
        if (current is null || !TryFlow(current))
        {
            Advance(frames);
            return;
        }

        if (scratch.Length < count)
            scratch = new float[count];

        int read = current.Samples.Read(scratch, 0, count);
        if (read < count)
        {
            Array.Clear(scratch, read, count - read);
            flowing = false;
            Post(() => SetState(DeckState.Buffering));
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

    bool TryFlow(Pipe current)
    {
        if (flowing)
            return true;

        double need = started ? Rebuffer : StartBuffer;
        if (current.Buffer.BufferedDuration.TotalSeconds < need)
            return false;

        flowing = true;
        if (!started)
        {
            started = true;
            Post(() =>
            {
                HasStarted = true;
                SetState(DeckState.Playing);
                Ready?.Invoke(this);
            });
        }
        else
        {
            duck = 0f;
            Post(() => SetState(DeckState.Playing));
        }
        return true;
    }

    void Advance(int frames)
    {
        for (int i = 0; i < frames && amount != target; i++)
            Tick();
    }

    void Tick()
    {
        if (amount < target)
            amount = MathF.Min(target, amount + step);
        else if (amount > target)
            amount = MathF.Max(target, amount - step);
    }

    void Run()
    {
        var token = cancel.Token;
        int attempt = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var url = StreamResolver.Resolve(Station, attempt, token);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Icy-MetaData", "1");

                using var reply = Net.Client.Send(request, HttpCompletionOption.ResponseHeadersRead, token);
                reply.EnsureSuccessStatusCode();
                response = reply;

                int interval = reply.Headers.TryGetValues("icy-metaint", out var values) &&
                               int.TryParse(values.FirstOrDefault(), out var parsed) ? parsed : 0;

                using var stream = new IcyStream(reply.Content.ReadAsStream(token), interval);
                stream.MetadataChanged += meta => Post(() => OnMetadata(meta));
                icy = stream;
                Decode(stream, token);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
            }
            finally
            {
                icy = null;
                response = null;
            }

            if (token.IsCancellationRequested)
                return;

            attempt++;
            failures++;
            if (!started && failures >= MaxConnectAttempts)
            {
                Post(() => SetState(DeckState.Failed));
                return;
            }

            Post(() => SetState(DeckState.Buffering));
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Min(20, 1.5 * failures)));
        }
    }

    void Decode(Stream stream, CancellationToken token)
    {
        IMp3FrameDecompressor? decoder = null;
        int decoderRate = 0, decoderChannels = 0;
        var pcm = new byte[65536];

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (pipe is { } full && full.Buffer.BufferedDuration.TotalSeconds > 20)
                {
                    Thread.Sleep(150);
                    continue;
                }

                Mp3Frame? frame;
                try { frame = Mp3Frame.LoadFromStream(stream); }
                catch (EndOfStreamException) { return; }
                if (frame is null)
                    return;

                int channels = frame.ChannelMode == ChannelMode.Mono ? 1 : 2;
                if (decoder is null || decoderRate != frame.SampleRate || decoderChannels != channels)
                {
                    decoder?.Dispose();
                    decoder = new AcmMp3FrameDecompressor(
                        new Mp3WaveFormat(frame.SampleRate, channels, frame.FrameLength, frame.BitRate));
                    decoderRate = frame.SampleRate;
                    decoderChannels = channels;
                    Prepare(decoder.OutputFormat);
                }

                int bytes = decoder.DecompressFrame(frame, pcm, 0);
                if (bytes > 0)
                    pipe!.Buffer.AddSamples(pcm, 0, bytes);
                failures = 0;
            }
        }
        finally
        {
            decoder?.Dispose();
        }
    }

    void Prepare(WaveFormat format)
    {
        if (pipe is { } existing &&
            existing.Buffer.WaveFormat.SampleRate == format.SampleRate &&
            existing.Buffer.WaveFormat.Channels == format.Channels)
            return;

        var buffer = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(30),
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };

        ISampleProvider samples = buffer.ToSampleProvider();
        if (format.Channels == 1)
            samples = new MonoToStereoSampleProvider(samples);
        if (format.SampleRate != SampleRate)
            samples = new WdlResamplingSampleProvider(samples, SampleRate);

        pipe = new Pipe(buffer, samples);
    }

    void CheckStall(object? _)
    {
        if (icy is { } stream && Environment.TickCount64 - stream.LastDataTick > StallTimeoutMs)
            response?.Dispose();
    }

    void OnMetadata(IcyMetadata meta)
    {
        Metadata = meta;
        MetadataChanged?.Invoke(this);
    }

    void SetState(DeckState state)
    {
        if (State == state || cancel.IsCancellationRequested)
            return;
        State = state;
        StateChanged?.Invoke(this);
    }

    void Post(Action action) => ui.Post(_ =>
    {
        if (!cancel.IsCancellationRequested)
            action();
    }, null);

    public void Dispose()
    {
        if (cancel.IsCancellationRequested)
            return;
        cancel.Cancel();
        watchdog.Dispose();
        response?.Dispose();
    }
}
