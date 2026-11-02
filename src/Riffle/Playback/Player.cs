using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;
using Riffle.Core;
using Riffle.Services;
using Riffle.Themes;
using Windows.Media;

namespace Riffle.Playback;

public sealed class SourceTile : INotifyPropertyChanged
{
    bool selected;
    string status = "";

    public SourceTile(Source source) => Source = source;

    public Source Source { get; }
    public string Title => Source.Title;
    public string Owner => Source.Owner;
    public string? Art => Source.Art;
    public int Count => Source.Tracks.Count;
    public bool Favored => Source.Weight > 1;
    public string Weight => Source.Weight switch { >= 4 => "Most", >= 2 => "More", _ => "" };

    public bool Selected { get => selected; set { selected = value; Changed(); } }
    public string Status { get => status; set { status = value; Changed(); Changed(nameof(Busy)); Changed(nameof(Subtitle)); } }
    public bool Busy => status.Length > 0;
    public string Subtitle => Busy ? status : $"{Count:N0} songs";

    public void Refresh()
    {
        Changed(nameof(Title)); Changed(nameof(Owner)); Changed(nameof(Art)); Changed(nameof(Count));
        Changed(nameof(Subtitle)); Changed(nameof(Favored)); Changed(nameof(Weight));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class Player : INotifyPropertyChanged, IDisposable
{
    const int MaxFailuresInARow = 10;
    const double ManualFade = 1.2;

    readonly Settings settings;
    readonly SynchronizationContext ui;
    readonly PlayQueue queue;
    readonly DispatcherTimer watchdog = new() { Interval = TimeSpan.FromSeconds(4) };
    readonly DispatcherTimer saver = new() { Interval = TimeSpan.FromSeconds(20) };
    readonly DispatcherTimer sleepTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly Dictionary<string, (SourceTile Tile, List<Track> Parts, TaskCompletionSource<bool> Done)> collecting = [];
    MediaControls? media;
    CancellationTokenSource? artRequest;
    CancellationTokenSource? noticeTimer;
    Artwork art = ArtworkService.Placeholder("riffle");
    Track? current;
    float[] waveform = [];
    bool playing, loading, handedOff, counted, signedIn, scrubbing;
    long? preloaded;
    int failures;
    double position, duration;
    long lastProgress;
    string? notice;
    DateTime? sleepAt;
    string? listenLabel;

    public Player(Settings settings, Library library, PlayerHost host, SynchronizationContext ui)
    {
        this.settings = settings;
        Library = library;
        Host = host;
        this.ui = ui;
        queue = new PlayQueue(Order);

        host.BlockAds = settings.BlockAds;
        host.Message += OnMessage;
        host.AdBlocked += () => Changed(nameof(AdsBlocked));

        foreach (var source in library.Sources)
            Tiles.Add(new SourceTile(source) { Selected = library.Selected.Contains(source.Id) });

        if (library.Queue is { Order.Count: > 0 } state)
        {
            queue.Restore(state);
            if (queue.Current is { } id && library.Tracks.TryGetValue(id, out var track))
            {
                current = track;
                position = library.ResumeMs;
                duration = track.Seconds * 1000.0;
                _ = LoadTrackVisualsAsync(track);
            }
        }

        watchdog.Tick += (_, _) => CheckStalled();
        saver.Tick += (_, _) => SaveProgress();
        sleepTimer.Tick += (_, _) => CheckSleep();
        saver.Start();
        Tiles.CollectionChanged += (_, _) => Changed(nameof(HasSources));
        Theme.Apply(Palette.Default);
    }

    public PlayerHost Host { get; }
    public Library Library { get; }
    public System.Collections.ObjectModel.ObservableCollection<SourceTile> Tiles { get; } = [];
    public PlayQueue Queue => queue;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? ArtChanged;
    public event Action? QueueChanged;
    public event Action? TrackChanged;
    public event Action? WaveformChanged;

    public float[] Spectrum { get; private set; } = new float[24];
    public Artwork Art => art;
    public Track? Current => current;
    public float[] Waveform => waveform;
    public string Title => current?.Title ?? "Nothing playing";
    public string Artist => current?.Artist ?? "Add a playlist to start";
    public string? TrackLink => current?.Link;
    public bool IsPlaying => playing;
    public bool IsLoading => loading;
    public bool HasSources => Tiles.Count > 0;
    public bool HasQueue => queue.Count > 0;
    public double Position => position;
    public double Duration => duration;
    public double Progress => duration > 0 ? Math.Clamp(position / duration, 0, 1) : 0;
    public string Elapsed => Clock(position);
    public string Total => Clock(duration);
    public int Round => queue.Round;
    public int RoundPlayed => queue.Count == 0 ? 0 : queue.Played + 1;
    public int RoundSize => queue.Count;
    public int AdsBlocked => Host.AdsBlocked;
    public bool SignedIn { get => signedIn; private set { signedIn = value; Changed(); } }
    public string Notice => notice ?? "";
    public bool IsSleeping => sleepAt is not null;
    public string SleepLabel => sleepAt is { } at ? $"Stops in {Math.Max(1, (int)Math.Ceiling((at - DateTime.Now).TotalMinutes))} min" : "Sleep timer";

    public string RoundLabel => queue.Count == 0 ? "" : $"Round {queue.Round}  ·  {RoundPlayed:N0} of {queue.Count:N0}";
    public string NowFrom => listenLabel ?? "";

    public Library.Pool SelectionPool => Library.BuildPool(Library.Selected, settings.CatchReuploads, settings.LongestMinutes);

    public string ShuffleLabel
    {
        get
        {
            var count = SelectionPool.Tracks.Count;
            return count == 0 ? "Pick playlists to shuffle" : $"Shuffle {count:N0} songs";
        }
    }

    public string SelectionDetail
    {
        get
        {
            var pool = SelectionPool;
            var parts = new List<string>();
            int picked = Library.Selected.Count;
            if (picked > 1)
                parts.Add($"{picked} playlists merged");
            if (pool.Duplicates > 0)
                parts.Add($"{pool.Duplicates:N0} duplicate{(pool.Duplicates == 1 ? "" : "s")} removed");
            if (pool.Unplayable > 0)
                parts.Add($"{pool.Unplayable:N0} preview only");
            return string.Join("  ·  ", parts);
        }
    }

    public double Volume
    {
        get => settings.Volume;
        set
        {
            value = Math.Clamp(Math.Round(value, 3), 0, 1);
            if (Math.Abs(settings.Volume - value) < 0.0005)
                return;
            settings.Volume = value;
            Host.Send(new { cmd = "volume", v = value });
            Changed();
        }
    }

    public double Crossfade
    {
        get => settings.Crossfade;
        set { settings.Crossfade = Math.Round(value); settings.Save(); Changed(); Changed(nameof(CrossfadeLabel)); }
    }

    public string CrossfadeLabel => settings.Crossfade <= 0 ? "Off" : $"{settings.Crossfade:0} s";

    public string Mode
    {
        get => settings.Mode;
        set
        {
            if (settings.Mode == value)
                return;
            settings.Mode = value;
            settings.Save();
            Changed();
            queue.ReshuffleUpcoming();
            AfterQueueEdit();
        }
    }

    public bool CatchReuploads
    {
        get => settings.CatchReuploads;
        set { settings.CatchReuploads = value; settings.Save(); Changed(); SelectionChanged(); RefreshPool(); }
    }

    public int LongestMinutes
    {
        get => settings.LongestMinutes;
        set { settings.LongestMinutes = value; settings.Save(); Changed(); SelectionChanged(); RefreshPool(); }
    }

    public bool ShowTile { get => settings.ShowTile; set { settings.ShowTile = value; settings.Save(); Changed(); } }
    public bool ResumeOnStart { get => settings.ResumeOnStart; set { settings.ResumeOnStart = value; settings.Save(); Changed(); } }

    public bool FollowCover
    {
        get => settings.FollowCover;
        set { settings.FollowCover = value; settings.Save(); Theme.Apply(value ? art.Palette : Palette.Default); Changed(); }
    }

    public bool BlockAds
    {
        get => settings.BlockAds;
        set { settings.BlockAds = value; Host.BlockAds = value; settings.Save(); Changed(); }
    }

    public bool EvenLoudness
    {
        get => settings.EvenLoudness;
        set { settings.EvenLoudness = value; settings.Save(); Changed(); PushTap(); }
    }

    public bool StartWithWindows
    {
        get => Autostart.Enabled;
        set { try { Autostart.Enabled = value; } catch (Exception) { } Changed(); }
    }

    public string EqPreset
    {
        get => settings.EqPreset;
        set
        {
            settings.EqPreset = value;
            settings.Eq = value switch
            {
                "Warm" => [3, 1.5, 0, -1, -2],
                "Bass" => [6, 3, 0, 0, 0],
                "Clear" => [-1, -1, 0, 2.5, 3.5],
                "Night" => [-3, 0, 1, -2, -5],
                _ => [0, 0, 0, 0, 0],
            };
            settings.Save();
            Changed();
            Changed(nameof(EqValues));
            PushTap();
        }
    }

    public double[] EqValues => settings.Eq;

    public void SetEqBand(int band, double db)
    {
        settings.Eq[band] = Math.Round(db * 2) / 2;
        settings.EqPreset = "Custom";
        settings.Save();
        Changed(nameof(EqPreset));
        PushTap();
    }

    void PushTap() => Host.Send(new { cmd = "tap", settings = new { eq = settings.Eq, level = settings.EvenLoudness } });

    public async Task StartUpAsync(bool resume)
    {
        Host.Send(new { cmd = "volume", v = settings.Volume });
        PushTap();
        SignedIn = await Host.IsSignedInAsync();
        if (resume && settings.ResumeOnStart && settings.WasPlaying && current is not null)
            Resume();
    }

    public async Task RefreshSignInAsync()
    {
        SignedIn = await Host.IsSignedInAsync();
    }

    public void SignOut()
    {
        Host.SignOut();
        SignedIn = false;
    }

    public void AttachMediaControls(IntPtr window)
    {
        try
        {
            media = new MediaControls(window, ui);
            media.ButtonPressed += button =>
            {
                switch (button)
                {
                    case SystemMediaTransportControlsButton.Play: Resume(); break;
                    case SystemMediaTransportControlsButton.Pause: Pause(); break;
                    case SystemMediaTransportControlsButton.Next: Next(); break;
                    case SystemMediaTransportControlsButton.Previous: Previous(); break;
                }
            };
        }
        catch (Exception)
        {
            media = null;
        }
    }

    // ---- shuffle ----

    List<long> Order(IReadOnlyCollection<long> ids)
    {
        var tracks = ids.Where(Library.Tracks.ContainsKey).Select(id => Library.Tracks[id]).ToList();
        var mode = Enum.TryParse<ShuffleMode>(settings.Mode, out var parsed) ? parsed : ShuffleMode.True;
        return Shuffler.Order(tracks, mode, t => Library.WeightOf(t.Id), t => Library.Plays.GetValueOrDefault(t.Id))
            .Select(t => t.Id).ToList();
    }

    public void ShuffleSelection()
    {
        var pool = SelectionPool.Tracks;
        if (pool.Count == 0)
            return;
        queue.Start(pool.Select(t => t.Id).ToList());
        failures = 0;
        listenLabel = Library.Selected.Count == 1
            ? Library.Sources.FirstOrDefault(s => s.Id == Library.Selected[0])?.Title
            : $"{Library.Selected.Count} playlists merged";
        Changed(nameof(NowFrom));
        PlayCurrent(fade: settings.Crossfade > 0 ? ManualFade : 0);
        AfterQueueEdit();
    }

    public void Reshuffle()
    {
        queue.ReshuffleUpcoming();
        preloaded = null;
        AfterQueueEdit();
        PreloadNext();
        ShowNotice("Reshuffled what's left");
    }

    void RefreshPool()
    {
        if (queue.Count == 0)
            return;
        queue.Merge(SelectionPool.Tracks.Select(t => t.Id).ToList());
        AfterQueueEdit();
    }

    void AfterQueueEdit()
    {
        Library.Queue = queue.Save();
        preloaded = null;
        Changed(nameof(HasQueue));
        Changed(nameof(RoundLabel));
        QueueChanged?.Invoke();
        if (playing)
            PreloadNext();
    }

    // ---- playback ----

    void PlayCurrent(double fade, int at = 0)
    {
        if (queue.Current is not { } id || !Library.Tracks.TryGetValue(id, out var track))
            return;

        SetCurrent(track);
        position = at;
        loading = true;
        handedOff = false;
        counted = false;
        lastProgress = Environment.TickCount64;
        watchdog.Start();
        Changed(nameof(IsLoading));
        Host.Send(new { cmd = "play", id, url = Url(track), fade, at });
    }

    void SetCurrent(Track track)
    {
        current = track;
        duration = track.Seconds * 1000.0;
        position = 0;
        waveform = [];
        Changed(nameof(Title));
        Changed(nameof(Artist));
        Changed(nameof(Current));
        Changed(nameof(TrackLink));
        Changed(nameof(RoundLabel));
        TimeChanged();
        WaveformChanged?.Invoke();
        TrackChanged?.Invoke();
        _ = LoadTrackVisualsAsync(track);
        PushMedia();
    }

    static string Url(Track track) => track.Link ?? $"https://api.soundcloud.com/tracks/{track.Id}";

    async Task LoadTrackVisualsAsync(Track track)
    {
        artRequest?.Cancel();
        var cts = artRequest = new CancellationTokenSource();
        _ = LoadWaveformAsync(track, cts);

        var next = await ArtworkService.LoadAsync(track.Art, cts.Token) ?? ArtworkService.Placeholder(track.Artist);
        if (cts.IsCancellationRequested)
            return;

        art = next;
        Changed(nameof(Art));
        ArtChanged?.Invoke();
        PushMedia();
        try { Theme.Apply(settings.FollowCover ? art.Palette : Palette.Default); }
        catch (InvalidOperationException) { }
    }

    async Task LoadWaveformAsync(Track track, CancellationTokenSource cts)
    {
        var wave = await ArtworkService.WaveformAsync(track.Wave, cts.Token);
        if (cts.IsCancellationRequested || wave is null)
            return;
        waveform = wave;
        WaveformChanged?.Invoke();
    }

    public void Toggle()
    {
        if (playing)
            Pause();
        else
            Resume();
    }

    public void Pause()
    {
        if (!playing)
            return;
        SetPlaying(false);
        watchdog.Stop();
        Host.Send(new { cmd = "pause", fade = 0.25 });
        SaveProgress();
    }

    bool engineHasTrack;

    public void Resume()
    {
        if (playing)
            return;
        if (current is null || queue.Count == 0)
        {
            ShuffleSelection();
            return;
        }
        if (engineHasTrack)
        {
            SetPlaying(true);
            lastProgress = Environment.TickCount64;
            watchdog.Start();
            Host.Send(new { cmd = "resume" });
        }
        else
        {
            PlayCurrent(0, (int)position);
        }
    }

    public void Next()
    {
        if (queue.Count == 0)
            return;
        queue.Advance();
        PlayCurrent(settings.Crossfade > 0 ? ManualFade : 0);
        AfterAdvance();
    }

    public void Previous()
    {
        if (position > 6000)
        {
            Seek(0);
            return;
        }
        queue.Back();
        PlayCurrent(settings.Crossfade > 0 ? ManualFade : 0);
        AfterAdvance();
    }

    public void PlayFromQueue(long id)
    {
        queue.Jump(id);
        PlayCurrent(settings.Crossfade > 0 ? ManualFade : 0);
        AfterAdvance();
    }

    public void PlayNextInQueue(long id)
    {
        queue.PlayNext(id);
        AfterQueueEdit();
        ShowNotice("Plays next");
    }

    public void NeverPlay(long id)
    {
        Library.NeverPlay.Add(id);
        bool isCurrent = current?.Id == id;
        queue.Remove(id);
        if (isCurrent && queue.Count > 0)
        {
            PlayCurrent(ManualFade);
            AfterAdvance();
        }
        AfterQueueEdit();
        Library.Save();
        Changed(nameof(NeverCount));
        ShowNotice("Won't play again");
    }

    public void NeverPlayCurrent()
    {
        if (current is { } track)
            NeverPlay(track.Id);
    }

    public int NeverCount => Library.NeverPlay.Count;

    public void ClearNever()
    {
        Library.NeverPlay.Clear();
        Library.Save();
        Changed(nameof(NeverCount));
        SelectionChanged();
        RefreshPool();
    }

    void AfterAdvance()
    {
        preloaded = null;
        Library.Queue = queue.Save();
        Changed(nameof(RoundLabel));
        QueueChanged?.Invoke();
    }

    // while the progress bar is held the time follows the mouse, the player jumps on release
    public void ScrubTo(double fraction)
    {
        scrubbing = true;
        position = Math.Clamp(fraction, 0, 1) * duration;
        TimeChanged();
    }

    public void Seek(double ms)
    {
        scrubbing = false;
        position = ms;
        handedOff = false;
        TimeChanged();
        Host.Send(new { cmd = "seek", ms });
    }

    public void SeekFraction(double fraction) => Seek(Math.Clamp(fraction, 0, 1) * duration);

    void PreloadNext()
    {
        if (queue.PeekNext() is not { } id || preloaded == id || !Library.Tracks.TryGetValue(id, out var track))
            return;
        preloaded = id;
        Host.Send(new { cmd = "preload", id, url = Url(track) });
    }

    void OnMessage(JsonElement m)
    {
        switch (m.GetProperty("t").GetString())
        {
            case "playing":
                OnPlaying(m);
                break;
            case "progress":
                OnProgress(m);
                break;
            case "finished":
                OnFinished(m.GetProperty("id").GetInt64());
                break;
            case "failed":
                OnFailed(m.GetProperty("id").GetInt64(), m.GetProperty("why").GetString());
                break;
            case "spectrum":
                ReadSpectrum(m.GetProperty("bands"));
                break;
            case "collect-progress":
                if (collecting.TryGetValue(m.GetProperty("id").GetString()!, out var job))
                    job.Tile.Status = $"Loading {m.GetProperty("count").GetInt32():N0} songs";
                break;
            case "collect-part":
                if (collecting.TryGetValue(m.GetProperty("id").GetString()!, out var part))
                    foreach (var t in m.GetProperty("tracks").EnumerateArray())
                        part.Parts.Add(ReadTrack(t));
                break;
            case "collect-done":
                FinishCollect(m.GetProperty("id").GetString()!, true);
                break;
            case "collect-failed":
                FinishCollect(m.GetProperty("id").GetString()!, false);
                break;
        }
    }

    void OnPlaying(JsonElement m)
    {
        loading = false;
        engineHasTrack = true;
        failures = 0;
        Changed(nameof(IsLoading));
        SetPlaying(true);
        lastProgress = Environment.TickCount64;

        // SoundCloud's own details are always the freshest
        if (m.TryGetProperty("dur", out var dur) && dur.GetDouble() > 0)
        {
            duration = dur.GetDouble();
            TimeChanged();
        }

        PreloadNext();
    }

    void OnProgress(JsonElement m)
    {
        if (current is null || m.GetProperty("id").GetInt64() != current.Id)
            return;

        lastProgress = Environment.TickCount64;
        if (scrubbing)
            return;
        position = m.GetProperty("pos").GetDouble();
        TimeChanged();

        if (!counted && position > Math.Min(30000, duration * 0.5))
        {
            counted = true;
            Library.Plays[current.Id] = Library.Plays.GetValueOrDefault(current.Id) + 1;
            Library.TotalPlays++;
            Library.TotalMinutes += current.Seconds / 60.0;
        }

        // hand over to the next song early enough for the two to overlap
        double fade = settings.Crossfade;
        if (playing && !handedOff && fade > 0 && duration > (fade + 2) * 1000 && position >= duration - fade * 1000 - 300 && queue.PeekNext() == preloaded && preloaded is not null)
        {
            handedOff = true;
            queue.Advance();
            PlayCurrent(fade);
            AfterAdvance();
        }
    }

    void OnFinished(long id)
    {
        if (current?.Id != id || !playing)
            return;
        queue.Advance();
        PlayCurrent(0);
        AfterAdvance();
    }

    void OnFailed(long id, string? why)
    {
        if (current?.Id != id)
        {
            // only the song waiting in the wings failed, nothing is interrupted
            if (preloaded == id)
                preloaded = null;
            return;
        }

        // A song that can't play leaves this round without stopping the music.
        loading = false;
        Changed(nameof(IsLoading));
        queue.Remove(id);
        if (why == "preview" && Library.Tracks.TryGetValue(id, out var track))
        {
            track.Policy = "SNIP";
            Library.Save();
        }

        if (++failures >= MaxFailuresInARow || queue.Count == 0)
        {
            SetPlaying(false);
            watchdog.Stop();
            ShowNotice("Can't reach SoundCloud right now");
            return;
        }
        PlayCurrent(0);
        AfterAdvance();
    }

    void CheckStalled()
    {
        // nothing from the player for a long while: move on rather than sit in silence
        if (playing || loading)
        {
            if (Environment.TickCount64 - lastProgress > 25000 && current is { } track)
                OnFailed(track.Id, "stalled");
        }
    }

    void ReadSpectrum(JsonElement bands)
    {
        var next = new float[24];
        int i = 0;
        foreach (var value in bands.EnumerateArray())
        {
            if (i >= next.Length)
                break;
            next[i++] = value.GetSingle();
        }
        Spectrum = next;
    }

    void SetPlaying(bool value)
    {
        if (playing == value)
            return;
        playing = value;
        settings.WasPlaying = value;
        if (!value)
            Spectrum = new float[24];
        Changed(nameof(IsPlaying));
        media?.SetPlaying(value);
    }

    void TimeChanged()
    {
        Changed(nameof(Position));
        Changed(nameof(Duration));
        Changed(nameof(Progress));
        Changed(nameof(Elapsed));
        Changed(nameof(Total));
    }

    void PushMedia()
    {
        if (current is null)
            return;
        media?.Show(current.Title, current.Artist, listenLabel ?? "Riffle", art.Bytes);
    }

    static string Clock(double ms)
    {
        var s = (int)(ms / 1000);
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    // ---- library ----

    public void ToggleSource(SourceTile tile)
    {
        tile.Selected = !tile.Selected;
        if (tile.Selected)
            Library.Selected.Add(tile.Source.Id);
        else
            Library.Selected.Remove(tile.Source.Id);
        SelectionChanged();
    }

    public void SelectionChanged()
    {
        Changed(nameof(ShuffleLabel));
        Changed(nameof(SelectionDetail));
        Changed(nameof(CanShuffle));
        Library.Save();
    }

    public bool CanShuffle => SelectionPool.Tracks.Count > 0;

    public void SetWeight(SourceTile tile, double weight)
    {
        tile.Source.Weight = weight;
        tile.Refresh();
        Library.Save();
    }

    public void RemoveSource(SourceTile tile)
    {
        Tiles.Remove(tile);
        Library.Sources.Remove(tile.Source);
        Library.Selected.Remove(tile.Source.Id);
        var used = Library.Sources.SelectMany(s => s.Tracks).ToHashSet();
        foreach (var id in Library.Tracks.Keys.Where(id => !used.Contains(id)).ToList())
            Library.Tracks.Remove(id);
        foreach (var mix in Library.Mixes)
            mix.Sources.Remove(tile.Source.Id);
        SelectionChanged();
        RefreshPool();
    }

    public SavedMix SaveMix(string name)
    {
        var mix = new SavedMix { Name = name, Sources = Library.Selected.ToList() };
        Library.Mixes.Add(mix);
        Library.Save();
        Changed(nameof(Mixes));
        return mix;
    }

    public IReadOnlyList<SavedMix> Mixes => Library.Mixes;

    public void DeleteMix(SavedMix mix)
    {
        Library.Mixes.Remove(mix);
        Library.Save();
        Changed(nameof(Mixes));
    }

    public void ApplyMix(SavedMix mix)
    {
        Library.Selected = mix.Sources.ToList();
        foreach (var tile in Tiles)
            tile.Selected = Library.Selected.Contains(tile.Source.Id);
        SelectionChanged();
    }

    // Takes whatever link gets pasted. Returns a problem to show, or null when it worked.
    public async Task<string?> AddLinkAsync(string text, Action<string>? status = null)
    {
        var link = Links.Parse(text);
        switch (link.Kind)
        {
            case LinkKind.Invalid:
                return "That isn't a SoundCloud link";
            case LinkKind.Track:
                return "That's a single song. Paste a playlist or profile.";
            case LinkKind.Profile:
                return await ImportProfileAsync(link, null, status);
            default:
                await AddSourceAsync(link);
                return null;
        }
    }

    public Task<string?> ImportSignedInAsync(Action<string>? status) =>
        ImportProfileAsync(default, "https://soundcloud.com/you/sets", status);

    async Task<string?> ImportProfileAsync(SoundCloudLink profile, string? page, Action<string>? status)
    {
        status?.Invoke("Looking through the profile");
        List<string> found;
        try
        {
            found = await Host.ScanLinksAsync(page ?? profile.Url + "/sets", n => status?.Invoke($"Found {n} playlists"), CancellationToken.None);
        }
        catch (Exception)
        {
            found = [];
        }

        var links = found.Select(Links.Parse).Where(l => l.Kind == LinkKind.Playlist).ToList();
        var user = profile.User ?? Host.LastUser;
        if (page is null)
            links = links.Where(l => string.Equals(l.User, profile.User, StringComparison.OrdinalIgnoreCase)).ToList();

        if (!string.IsNullOrEmpty(user))
            links.Insert(0, new SoundCloudLink(LinkKind.Likes, $"https://soundcloud.com/{user}/likes", user));

        status?.Invoke("");
        var jobs = links.DistinctBy(l => l.Url).Select(AddSourceAsync).ToList();
        if (jobs.Count == 0)
            return "No playlists found there";
        await Task.WhenAll(jobs);
        return null;
    }

    async Task AddSourceAsync(SoundCloudLink link)
    {
        var existing = Library.Sources.FirstOrDefault(s => s.Url == link.Url);
        var source = existing ?? new Source { Url = link.Url, Title = Fallback(link), Owner = link.User ?? "" };
        SourceTile tile;
        if (existing is null)
        {
            Library.Sources.Add(source);
            tile = new SourceTile(source);
            Tiles.Add(tile);
        }
        else
        {
            tile = Tiles.First(t => t.Source == existing);
        }
        await SyncAsync(tile);
    }

    static string Fallback(SoundCloudLink link) => link.Kind switch
    {
        LinkKind.Likes => "Likes",
        LinkKind.Tracks => "Tracks",
        _ => link.Url.Split('/').Last().Replace('-', ' '),
    };

    // Only a few at once, the embed takes a moment per list and there's no rush.
    readonly SemaphoreSlim syncSlots = new(3);

    public async Task SyncAsync(SourceTile tile)
    {
        var source = tile.Source;
        if (collecting.ContainsKey(source.Id))
            return;

        tile.Status = "Waiting";
        var done = new TaskCompletionSource<bool>();
        collecting[source.Id] = (tile, [], done);

        await syncSlots.WaitAsync();
        try
        {
            tile.Status = "Loading";
            var (title, owner, picture) = await ArtworkService.DescribeAsync(source.Url, CancellationToken.None);
            if (title is not null && !source.Url.EndsWith("/likes", StringComparison.Ordinal) && !source.Url.EndsWith("/tracks", StringComparison.Ordinal))
                source.Title = title;
            if (owner is not null)
                source.Owner = owner;
            if (picture is not null)
                source.Art = picture;
            tile.Refresh();

            Host.Send(new { cmd = "collect", id = source.Id, url = source.Url });
            await done.Task;
        }
        finally
        {
            syncSlots.Release();
        }
    }

    public void SyncAll()
    {
        foreach (var tile in Tiles.ToList())
            _ = SyncAsync(tile);
    }

    void FinishCollect(string id, bool ok)
    {
        if (!collecting.Remove(id, out var job))
            return;

        var source = job.Tile.Source;
        if (ok && job.Parts.Count > 0)
        {
            source.Tracks = job.Parts.Select(t => t.Id).Distinct().ToList();
            foreach (var track in job.Parts)
                Library.Tracks[track.Id] = track;
            source.Synced = DateTime.UtcNow;
            if (source.Art is null)
                source.Art = job.Parts.FirstOrDefault(t => t.Art is not null)?.Art;
            job.Tile.Status = "";
            job.Tile.Refresh();

            // something new in a playlist that's playing joins the current round
            if (Library.Selected.Contains(id))
                RefreshPool();
            else if (Library.Selected.Count == 0 && Tiles.Count == 1)
                ToggleSource(job.Tile);
        }
        else
        {
            job.Tile.Status = source.Tracks.Count > 0 ? "" : "Couldn't load this one";
        }
        SelectionChanged();
        job.Done.TrySetResult(ok);
    }

    static Track ReadTrack(JsonElement t)
    {
        static string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new Track
        {
            Id = t.GetProperty("id").GetInt64(),
            Title = Text(t, "title") ?? "",
            Artist = Text(t, "artist") ?? "",
            Art = Text(t, "art"),
            Link = Text(t, "link"),
            Wave = Text(t, "wave"),
            Seconds = t.TryGetProperty("seconds", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0,
            Policy = Text(t, "policy"),
        };
    }

    // ---- sleep timer ----

    public void SleepIn(TimeSpan? span)
    {
        sleepAt = span is { } d ? DateTime.Now + d : null;
        if (sleepAt is null)
            sleepTimer.Stop();
        else
            sleepTimer.Start();
        Changed(nameof(IsSleeping));
        Changed(nameof(SleepLabel));
    }

    void CheckSleep()
    {
        Changed(nameof(SleepLabel));
        if (sleepAt is not { } at || DateTime.Now < at)
            return;
        SleepIn(null);
        SetPlaying(false);
        watchdog.Stop();
        Host.Send(new { cmd = "pause", fade = 20.0 });
    }

    void ShowNotice(string text)
    {
        noticeTimer?.Cancel();
        notice = text;
        Changed(nameof(Notice));
        var cts = noticeTimer = new CancellationTokenSource();
        Task.Delay(3000, cts.Token).ContinueWith(_ =>
        {
            notice = null;
            Changed(nameof(Notice));
        }, cts.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void SaveProgress()
    {
        Library.ResumeMs = (int)position;
        Library.Queue = queue.Count > 0 ? queue.Save() : null;
        if (playing || loading)
            Library.Save();
    }

    void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        settings.WasPlaying = playing;
        artRequest?.Cancel();
        noticeTimer?.Cancel();
        watchdog.Stop();
        saver.Stop();
        sleepTimer.Stop();
        Library.ResumeMs = (int)position;
        Library.Queue = queue.Count > 0 ? queue.Save() : null;
        Library.Save();
        settings.Save();
        media?.Dispose();
    }
}
