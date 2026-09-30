using Drift.Radio;
using Drift.Services;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Drift.Audio;

// Plays a shuffled run of YouTube videos: whole playlists, or songs looked up by name.
// The next track is fetched while the current one plays and they crossfade into each
// other, so it never stops between songs.
sealed class PlaylistSource : Source
{
    const double Lookahead = 0.6;
    const double SkipFade = 2.5;
    const int MaxFailuresInARow = 6;

    sealed record Item(VideoRef? Video, string? Song);

    sealed class Voice(Item item, TrackInfo info, MemoryStream data, WaveStream reader, ISampleProvider samples) : IDisposable
    {
        public Item Item { get; } = item;
        public TrackInfo Info { get; } = info;
        public ISampleProvider Samples { get; } = samples;
        public TimeSpan Remaining => reader.TotalTime - reader.CurrentTime;

        public void Dispose()
        {
            reader.Dispose();
            data.Dispose();
        }
    }

    readonly YouTube youtube;
    readonly Func<string, bool> isBlocked;
    readonly BufferedWaveProvider buffer;
    readonly ISampleProvider output;
    readonly object gate = new();
    readonly LinkedList<Item> upcoming = new();
    readonly List<Item> played = [];
    List<Item> all = [];
    volatile bool skipWanted;
    volatile bool backWanted;

    // audio thread only
    bool flowing;
    bool flowedOnce;

    public PlaylistSource(Station station, SynchronizationContext ui, YouTube youtube, Func<string, bool> isBlocked)
        : base(station, ui)
    {
        this.youtube = youtube;
        this.isBlocked = isBlocked;
        buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2))
        {
            BufferDuration = TimeSpan.FromSeconds(4),
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };
        output = buffer.ToSampleProvider();
    }

    public double Crossfade { get; set; } = 6;

    public override bool CanSkip => true;

    public override void Start() => Task.Run(RunAsync);

    public override void Skip() => skipWanted = true;

    public override void Back() => backWanted = true;

    protected override bool Render(float[] target, int count)
    {
        if (!flowing)
        {
            if (buffer.BufferedDuration.TotalSeconds < 0.3)
                return false;
            flowing = true;
            if (flowedOnce)
            {
                DuckIn();
                Post(() => SetState(SourceState.Playing));
            }
            flowedOnce = true;
        }

        int read = output.Read(target, 0, count);
        if (read < count)
        {
            Array.Clear(target, read, count - read);
            flowing = false;
            Post(() => SetState(SourceState.Buffering));
        }
        return true;
    }

    async Task RunAsync()
    {
        var token = cancel.Token;
        try
        {
            all = await BuildAsync(token, quick: true);
            _ = FillInAsync(token);
            if (all.Count == 0)
            {
                Post(() => SetState(SourceState.Failed));
                return;
            }

            var current = await LoadNextAsync(token);
            if (current is null)
            {
                Post(() => SetState(SourceState.Failed));
                return;
            }
            Became(current);

            Task<Voice?>? next = null;
            Voice? incoming = null;
            long fadeLength = 0, fadePosition = 0;
            var a = new float[4096];
            var b = new float[4096];
            var mixed = new float[4096];
            var bytes = new byte[4096 * 4];

            while (!token.IsCancellationRequested)
            {
                if (buffer.BufferedDuration.TotalSeconds > Lookahead)
                {
                    await Task.Delay(15, token);
                    continue;
                }

                if (backWanted && incoming is null)
                {
                    backWanted = false;
                    if (next is not null && await next is { } unused)
                    {
                        lock (gate)
                            upcoming.AddFirst(unused.Item);
                        unused.Dispose();
                    }
                    next = null;
                    if (StepBack(current.Item))
                        skipWanted = true;
                }

                if (next is null && incoming is null &&
                    (skipWanted || current.Remaining < TimeSpan.FromMinutes(2)))
                    next = LoadNextAsync(token);

                if (incoming is null && next is { IsCompleted: true } &&
                    (skipWanted || current.Remaining.TotalSeconds <= Crossfade))
                {
                    incoming = await next;
                    next = null;
                    double seconds = skipWanted ? SkipFade : Crossfade;
                    skipWanted = false;
                    if (incoming is not null)
                    {
                        fadeLength = Math.Max(1, (long)(seconds * SampleRate));
                        fadePosition = 0;
                        Became(incoming);
                    }
                }

                int got = current.Samples.Read(a, 0, a.Length);
                int length = got;

                if (incoming is not null)
                {
                    int got2 = incoming.Samples.Read(b, 0, b.Length);
                    if (got < a.Length)
                        Array.Clear(a, got, a.Length - got);
                    if (got2 < b.Length)
                        Array.Clear(b, got2, b.Length - got2);
                    length = Math.Max(got, got2);

                    for (int i = 0; i < length; i += 2)
                    {
                        float t = Math.Min(1f, (float)fadePosition / fadeLength);
                        float fadeOut = MathF.Cos(t * MathF.PI * 0.5f);
                        float fadeIn = MathF.Sin(t * MathF.PI * 0.5f);
                        mixed[i] = a[i] * fadeOut + b[i] * fadeIn;
                        mixed[i + 1] = a[i + 1] * fadeOut + b[i + 1] * fadeIn;
                        fadePosition++;
                    }
                    Write(mixed, length, bytes);

                    if (fadePosition >= fadeLength || got == 0)
                    {
                        current.Dispose();
                        current = incoming;
                        incoming = null;
                    }
                    continue;
                }

                if (got > 0)
                {
                    Write(a, got, bytes);
                    continue;
                }

                // the track ended before the next one was ready
                next ??= LoadNextAsync(token);
                var after = await next;
                next = null;
                current.Dispose();
                if (after is null)
                {
                    Post(() => SetState(SourceState.Failed));
                    return;
                }
                current = after;
                skipWanted = false;
                Became(current);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            Post(() => SetState(SourceState.Failed));
        }
    }

    void Write(float[] samples, int count, byte[] bytes)
    {
        Buffer.BlockCopy(samples, 0, bytes, 0, count * 4);
        buffer.AddSamples(bytes, 0, count * 4);
    }

    void Became(Voice voice)
    {
        lock (gate)
            played.Add(voice.Item);
        Post(() => SetTrack(voice.Info));
    }

    bool StepBack(Item current)
    {
        lock (gate)
        {
            if (played.Count < 2)
                return false;
            var previous = played[^2];
            played.RemoveRange(played.Count - 2, 2);
            upcoming.AddFirst(current);
            upcoming.AddFirst(previous);
            return true;
        }
    }

    async Task FillInAsync(CancellationToken token)
    {
        if (Station.Playlists.Count == 0)
            return;
        var full = await BuildAsync(token, quick: false);
        lock (gate)
        {
            if (full.Count > all.Count)
                all = full;
        }
    }

    async Task<List<Item>> BuildAsync(CancellationToken token, bool quick)
    {
        var items = new List<Item>();
        foreach (var playlist in Station.Playlists)
        {
            try
            {
                var videos = await youtube.PlaylistAsync(playlist, token, quick ? 30 : int.MaxValue);
                items.AddRange(videos.Select(v => new Item(v, null)));
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                // one playlist being gone shouldn't take the others with it
            }
        }
        items.AddRange(Station.Songs.Select(s => new Item(null, s)));
        return items;
    }

    Item Take()
    {
        lock (gate)
        {
            if (upcoming.Count == 0)
            {
                foreach (var item in all.OrderBy(_ => Random.Shared.Next()))
                    upcoming.AddLast(item);
            }
            var first = upcoming.First!.Value;
            upcoming.RemoveFirst();
            return first;
        }
    }

    async Task<Voice?> LoadNextAsync(CancellationToken token)
    {
        for (int attempt = 0; attempt < MaxFailuresInARow && !token.IsCancellationRequested; attempt++)
        {
            var item = Take();
            try
            {
                var video = item.Video ?? await youtube.FindSongAsync(item.Song!, token);
                if (video is null || isBlocked(video.Id) || (item.Song is not null && isBlocked(item.Song)))
                    continue;

                var data = await youtube.AudioAsync(video.Id, token);
                var reader = new StreamMediaFoundationReader(data);
                ISampleProvider samples = reader.ToSampleProvider();
                if (samples.WaveFormat.Channels == 1)
                    samples = new MonoToStereoSampleProvider(samples);
                if (samples.WaveFormat.SampleRate != SampleRate)
                    samples = new WdlResamplingSampleProvider(samples, SampleRate);

                var resolved = item.Video is null ? item with { Video = video } : item;
                return new Voice(resolved, YouTube.Describe(video, item.Song), data, reader, samples);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // unavailable, age restricted or removed: try the next one
            }
        }
        return null;
    }
}
