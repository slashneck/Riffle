using Drift.Radio;
using Drift.Services;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Drift.Audio;

// One live connection to a radio station. Network and decoding run on their own thread
// and fill a buffer that the mixer pulls from.
sealed class RadioSource : Source
{
    const double StartBuffer = 2.0;
    const double Rebuffer = 3.0;
    const int StallTimeoutMs = 12000;
    const int MaxConnectAttempts = 3;

    sealed record Pipe(BufferedWaveProvider Buffer, ISampleProvider Samples);

    readonly Thread worker;
    readonly Timer watchdog;
    volatile Pipe? pipe;
    volatile HttpResponseMessage? response;
    volatile IcyStream? icy;
    int failures;

    // audio thread only
    bool flowing;
    bool flowedOnce;

    public RadioSource(Station station, SynchronizationContext ui) : base(station, ui)
    {
        worker = new Thread(Run) { IsBackground = true, Name = "Drift radio" };
        watchdog = new Timer(CheckStall, null, 2000, 2000);
    }

    public override void Start() => worker.Start();

    protected override bool Render(float[] buffer, int count)
    {
        var current = pipe;
        if (current is null)
            return false;

        if (!flowing)
        {
            double need = flowedOnce ? Rebuffer : StartBuffer;
            if (current.Buffer.BufferedDuration.TotalSeconds < need)
                return false;

            flowing = true;
            if (flowedOnce)
            {
                DuckIn();
                Post(() => SetState(SourceState.Playing));
            }
            flowedOnce = true;
        }

        int read = current.Samples.Read(buffer, 0, count);
        if (read < count)
        {
            Array.Clear(buffer, read, count - read);
            flowing = false;
            Post(() => SetState(SourceState.Buffering));
        }
        return true;
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
                stream.MetadataChanged += meta => Post(() => SetTrack(meta));
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
            if (!flowedOnce && failures >= MaxConnectAttempts)
            {
                Post(() => SetState(SourceState.Failed));
                return;
            }

            Post(() => SetState(SourceState.Buffering));
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

    public override void Dispose()
    {
        if (cancel.IsCancellationRequested)
            return;
        base.Dispose();
        watchdog.Dispose();
        response?.Dispose();
    }
}
