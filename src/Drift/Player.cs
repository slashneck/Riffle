using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Drift.Audio;
using Drift.Radio;
using Drift.Services;
using Drift.Themes;
using Windows.Media;

namespace Drift;

public sealed class Player : INotifyPropertyChanged, IDisposable
{
    const double SleepFade = 30;

    readonly Settings settings;
    readonly ArtworkService artwork = new();
    readonly SynchronizationContext ui;
    readonly DispatcherTimer sleepTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    readonly IReadOnlyList<Mood> builtIn;
    MediaControls? media;
    CancellationTokenSource? artRequest;
    CancellationTokenSource? noticeTimer;
    Mood mood;
    Station station;
    Artwork art;
    string title = "";
    string artist = "";
    string detail = "";
    string? notice;
    bool hasPlayed;
    DateTime? sleepAt;

    public Player(Settings settings, SynchronizationContext ui)
    {
        this.settings = settings;
        this.ui = ui;
        artwork.FindCovers = settings.FindCovers;

        builtIn = StationList.Load();
        Moods = new ObservableCollection<Mood>(builtIn);
        RebuildYours();
        mood = Moods.FirstOrDefault(m => m.Id == settings.MoodId) ?? Moods[0];
        station = RememberedStation(mood);

        Engine = new AudioEngine(ui)
        {
            Volume = (float)settings.Volume,
            Crossfade = settings.CrossfadeSeconds,
            IsBlocked = id => settings.Blocked.Contains(id),
        };
        Engine.StatusChanged += OnStatusChanged;
        Engine.TrackChanged += OnTrackChanged;
        Engine.StationFailed += OnStationFailed;
        Engine.StationRestored += OnStationRestored;

        sleepTimer.Tick += (_, _) => CheckSleep();

        art = artwork.Placeholder(station);
        ApplyColors();
        RefreshText();
        _ = LoadArtAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? TrackChanged;
    public event Action? ArtChanged;

    public AudioEngine Engine { get; }
    public ObservableCollection<Mood> Moods { get; }

    public Mood Mood
    {
        get => mood;
        set
        {
            if (value is not null)
                SelectMood(value);
        }
    }

    public Station Station
    {
        get => station;
        set
        {
            if (value is not null)
                Select(value);
        }
    }

    public IReadOnlyList<Station> Stations => mood.Stations;
    public Artwork Art => art;
    public BitmapSource Cover => art.Image;
    public BitmapSource Thumb => art.Thumb;
    public string Title => title;
    public string Artist => artist;
    public string Detail => detail;
    public bool IsPlaying => Engine.IsPlaying;
    public bool IsBusy => Engine.Status is PlayerStatus.Connecting or PlayerStatus.Buffering;
    public bool HasPlayed => hasPlayed;
    public bool PlaysSongs => station.Kind == "youtube";
    public bool CanBlock => PlaysSongs && AudibleTrack?.VideoId is not null;
    public bool IsSleeping => sleepAt is not null;
    public int BlockedCount => settings.Blocked.Count;
    public IReadOnlyList<CustomPlaylist> CustomPlaylists => settings.Playlists;

    public string SleepLabel => sleepAt is { } at
        ? $"Sleeping in {Math.Max(1, (int)Math.Ceiling((at - DateTime.Now).TotalMinutes))} min"
        : "Sleep timer";

    public double Volume
    {
        get => settings.Volume;
        set
        {
            value = Math.Clamp(Math.Round(value, 3), 0, 1);
            if (Math.Abs(settings.Volume - value) < 0.0005)
                return;
            settings.Volume = value;
            Engine.Volume = (float)value;
            Changed();
        }
    }

    public bool ShowInTaskbar
    {
        get => settings.ShowInTaskbar;
        set => Set(() => settings.ShowInTaskbar = value);
    }

    public bool FindCovers
    {
        get => settings.FindCovers;
        set
        {
            Set(() => settings.FindCovers = artwork.FindCovers = value);
            _ = LoadArtAsync();
        }
    }

    public bool ResumeOnStart
    {
        get => settings.ResumeOnStart;
        set => Set(() => settings.ResumeOnStart = value);
    }

    public bool StartWithWindows
    {
        get => Autostart.Enabled;
        set
        {
            try { Autostart.Enabled = value; }
            catch (Exception) { }
            Changed();
        }
    }

    public string Crossfade
    {
        get => settings.Crossfade;
        set
        {
            Set(() => settings.Crossfade = value);
            Engine.Crossfade = settings.CrossfadeSeconds;
        }
    }

    public bool FollowCover
    {
        get => settings.FollowCover;
        set
        {
            Set(() => settings.FollowCover = value);
            ApplyColors();
        }
    }

    void Set(Action change, [CallerMemberName] string? name = null)
    {
        change();
        settings.Save();
        Changed(name);
    }

    public void AttachMediaControls(IntPtr window)
    {
        try
        {
            media = new MediaControls(window, ui);
            media.ButtonPressed += OnMediaButton;
        }
        catch (Exception)
        {
            media = null;
        }
    }

    public void StartUp(bool resume)
    {
        if (resume && settings.ResumeOnStart && settings.WasPlaying)
            Play();
    }

    public void Toggle()
    {
        if (Engine.IsPlaying)
            Pause();
        else
            Play();
    }

    public void Play()
    {
        if (!hasPlayed)
        {
            hasPlayed = true;
            Changed(nameof(HasPlayed));
        }
        Engine.Play(station);
        PushMedia();
    }

    public void Pause() => Engine.Pause();

    public void Next()
    {
        if (PlaysSongs && Engine.Station == station && Engine.IsPlaying)
            Engine.Skip();
        else
            Step(1);
    }

    public void Previous()
    {
        if (PlaysSongs && Engine.Station == station && Engine.IsPlaying)
            Engine.Back();
        else
            Step(-1);
    }

    void Step(int direction)
    {
        var list = mood.Stations;
        int index = list.IndexOf(station);
        Select(list[(index + direction + list.Count) % list.Count]);
    }

    public void Select(Station next)
    {
        if (next == station && Engine.IsPlaying)
            return;

        if (next.Mood != mood)
        {
            mood = next.Mood;
            Changed(nameof(Mood));
            Changed(nameof(Stations));
        }

        station = next;
        Remember();
        Changed(nameof(Station));
        Changed(nameof(PlaysSongs));

        Play();
        RefreshText();
        _ = LoadArtAsync();
    }

    // picking a mood means "play this now"
    public void SelectMood(Mood next)
    {
        if (next == mood)
        {
            if (!Engine.IsPlaying)
                Play();
            return;
        }

        mood = next;
        station = RememberedStation(next);
        Remember();
        Changed(nameof(Mood));
        Changed(nameof(Stations));
        Changed(nameof(Station));
        Changed(nameof(PlaysSongs));

        Play();
        RefreshText();
        _ = LoadArtAsync();
    }

    void Remember()
    {
        settings.MoodId = mood.Id;
        settings.StationByMood[mood.Id] = station.Id;
        settings.Save();
    }

    Station RememberedStation(Mood target) =>
        settings.StationByMood.TryGetValue(target.Id, out var id) && target.Stations.FirstOrDefault(s => s.Id == id) is { } found
            ? found
            : target.Stations[0];

    // "don't play this again" only exists for songs, radio can't skip anyway
    public void BlockCurrent()
    {
        if (AudibleTrack?.VideoId is not { } id || settings.Blocked.Contains(id))
            return;
        settings.Blocked.Add(id);
        settings.Save();
        Changed(nameof(BlockedCount));
        Engine.Skip();
        ShowNotice("Won't play that one again");
    }

    public void ClearBlocked()
    {
        settings.Blocked.Clear();
        settings.Save();
        Changed(nameof(BlockedCount));
    }

    public void SleepIn(TimeSpan? duration)
    {
        sleepAt = duration is { } d ? DateTime.Now + d : null;
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

        // drift off slowly instead of cutting out
        SleepIn(null);
        Engine.Pause(SleepFade);
    }

    public async Task<string?> AddPlaylistAsync(string link, string? name)
    {
        var id = PlaylistId(link);
        if (id is null)
            return "That doesn't look like a YouTube playlist link.";
        if (settings.Playlists.Any(p => p.Id == id))
            return "That playlist is already in Drift.";

        string title;
        try
        {
            var client = new YoutubeExplode.YoutubeClient();
            var playlist = await client.Playlists.GetAsync(id);
            title = string.IsNullOrWhiteSpace(name) ? playlist.Title : name.Trim();
        }
        catch (Exception)
        {
            return "Couldn't open that playlist. Is it public?";
        }

        settings.Playlists.Add(new CustomPlaylist(title, id));
        settings.Save();
        RebuildYours();
        Changed(nameof(CustomPlaylists));
        return null;
    }

    public void RemovePlaylist(CustomPlaylist playlist)
    {
        settings.Playlists.Remove(playlist);
        settings.Save();
        bool wasCurrent = station.IsCustom && station.Playlists.Contains(playlist.Id);
        RebuildYours();
        Changed(nameof(CustomPlaylists));
        if (wasCurrent)
            SelectMood(Moods[0]);
    }

    static string? PlaylistId(string link)
    {
        link = link.Trim();
        if (Uri.TryCreate(link, UriKind.Absolute, out var uri) &&
            (uri.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) || uri.Host == "youtu.be"))
        {
            var list = System.Web.HttpUtility.ParseQueryString(uri.Query)["list"];
            return string.IsNullOrWhiteSpace(list) ? null : list;
        }
        return link.StartsWith("PL", StringComparison.Ordinal) && link.Length > 12 && !link.Contains(' ') ? link : null;
    }

    void RebuildYours()
    {
        var old = Moods.FirstOrDefault(m => m.Id == "yours");
        if (old is not null)
            Moods.Remove(old);
        if (settings.Playlists.Count == 0)
            return;

        var mine = new Mood
        {
            Id = "yours",
            Name = "Yours",
            Stations = settings.Playlists.Select(p => new Station
            {
                Id = "yours-" + p.Id,
                Name = p.Name,
                Blurb = "Your playlist",
                Source = "YouTube",
                Kind = "youtube",
                Playlists = [p.Id],
                Art = "lookup",
                Tint = TintFor(p.Id),
                IsCustom = true,
            }).ToList(),
        };
        foreach (var s in mine.Stations)
            s.Mood = mine;
        Moods.Add(mine);
    }

    static string TintFor(string id)
    {
        var color = Palette.FromHsl(Math.Abs(id.GetHashCode()) % 360, 0.45, 0.6);
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    void OnStatusChanged()
    {
        settings.WasPlaying = Engine.IsPlaying;
        Changed(nameof(IsPlaying));
        Changed(nameof(IsBusy));
        RefreshText();
        media?.SetPlaying(Engine.IsPlaying);
    }

    void OnTrackChanged()
    {
        RefreshText();
        Changed(nameof(CanBlock));
        TrackChanged?.Invoke();
        _ = LoadArtAsync();
    }

    void OnStationFailed(Station failed) => ShowNotice($"Couldn't reach {failed.Title}");

    void OnStationRestored()
    {
        if (Engine.Station is { } restored)
        {
            mood = restored.Mood;
            station = restored;
            Changed(nameof(Mood));
            Changed(nameof(Stations));
            Changed(nameof(Station));
            Changed(nameof(PlaysSongs));
            RefreshText();
            _ = LoadArtAsync();
        }
    }

    void OnMediaButton(SystemMediaTransportControlsButton button)
    {
        switch (button)
        {
            case SystemMediaTransportControlsButton.Play: Play(); break;
            case SystemMediaTransportControlsButton.Pause: Pause(); break;
            case SystemMediaTransportControlsButton.Next: Next(); break;
            case SystemMediaTransportControlsButton.Previous: Previous(); break;
        }
    }

    void ShowNotice(string text)
    {
        noticeTimer?.Cancel();
        notice = text;
        RefreshText();

        var cts = noticeTimer = new CancellationTokenSource();
        Task.Delay(4500, cts.Token).ContinueWith(_ =>
        {
            notice = null;
            RefreshText();
        }, cts.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    TrackInfo? AudibleTrack =>
        Engine.Station == station && Engine.Track is { HasTrack: true, IsAdvert: false } track ? track : null;

    void RefreshText()
    {
        var track = AudibleTrack;
        SetText(ref title, track?.Title ?? station.Title, nameof(Title));
        SetText(ref artist, track?.Artist ?? (track is null ? station.Blurb : station.Title), nameof(Artist));

        string line = notice ?? Engine.Status switch
        {
            PlayerStatus.Connecting when Engine.Station == station => PlaysSongs ? "Loading songs" : "Tuning in",
            PlayerStatus.Buffering => "Buffering",
            _ => $"{station.Title}  ·  {station.Source}",
        };
        SetText(ref detail, line, nameof(Detail));
        PushMedia();
    }

    async Task LoadArtAsync()
    {
        artRequest?.Cancel();
        var cts = artRequest = new CancellationTokenSource();
        var forStation = station;

        Artwork next;
        try
        {
            next = await artwork.ResolveAsync(forStation, AudibleTrack, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested || forStation != station || next == art)
            return;

        art = next;
        ApplyColors();
        Changed(nameof(Art));
        Changed(nameof(Cover));
        Changed(nameof(Thumb));
        ArtChanged?.Invoke();
        PushMedia();
    }

    void ApplyColors() => Theme.Apply(settings.FollowCover ? art.Palette : Palette.Default);

    void PushMedia()
    {
        if (media is null || !hasPlayed)
            return;
        media.Show(title, AudibleTrack is null ? station.Source : artist, station.Title, art.Bytes);
        media.SetPlaying(Engine.IsPlaying);
    }

    void SetText(ref string field, string value, string name)
    {
        if (field == value)
            return;
        field = value;
        Changed(name);
    }

    void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Save() => settings.Save();

    public void Dispose()
    {
        settings.WasPlaying = Engine.IsPlaying;
        artRequest?.Cancel();
        noticeTimer?.Cancel();
        sleepTimer.Stop();
        Engine.Dispose();
        media?.Dispose();
        settings.Save();
    }
}
