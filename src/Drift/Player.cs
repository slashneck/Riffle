using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using Drift.Audio;
using Drift.Radio;
using Drift.Services;
using Drift.Themes;
using Windows.Media;

namespace Drift;

public sealed class Player : INotifyPropertyChanged, IDisposable
{
    readonly Settings settings;
    readonly ArtworkService artwork = new();
    readonly SynchronizationContext ui;
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

    public Player(Settings settings, SynchronizationContext ui)
    {
        this.settings = settings;
        this.ui = ui;
        artwork.FindCovers = settings.FindCovers;

        Moods = StationList.Load();
        mood = Moods.FirstOrDefault(m => m.Id == settings.MoodId) ?? Moods[0];
        station = RememberedStation(mood);

        Engine = new AudioEngine(ui) { Volume = (float)settings.Volume };
        Engine.StatusChanged += OnStatusChanged;
        Engine.TrackChanged += OnTrackChanged;
        Engine.StationFailed += OnStationFailed;
        Engine.StationRestored += OnStationRestored;

        art = artwork.Placeholder(station);
        Theme.Apply(art.Palette);
        RefreshText();
        _ = LoadArtAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? TrackChanged;
    public event Action? ArtChanged;

    public AudioEngine Engine { get; }
    public IReadOnlyList<Mood> Moods { get; }

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
        set
        {
            settings.ShowInTaskbar = value;
            settings.Save();
            Changed();
        }
    }

    public bool FindCovers
    {
        get => settings.FindCovers;
        set
        {
            settings.FindCovers = value;
            artwork.FindCovers = value;
            settings.Save();
            Changed();
            _ = LoadArtAsync();
        }
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

    public void Toggle()
    {
        if (Engine.IsPlaying)
            Engine.Pause();
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

    public void Next() => Step(1);

    public void Previous() => Step(-1);

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
        settings.MoodId = mood.Id;
        settings.StationByMood[mood.Id] = station.Id;
        Changed(nameof(Station));

        Play();
        RefreshText();
        _ = LoadArtAsync();
    }

    void SelectMood(Mood next)
    {
        if (next == mood)
            return;

        mood = next;
        station = RememberedStation(next);
        settings.MoodId = next.Id;
        Changed(nameof(Mood));
        Changed(nameof(Stations));
        Changed(nameof(Station));

        if (Engine.IsPlaying)
            Engine.Play(station);
        RefreshText();
        _ = LoadArtAsync();
        PushMedia();
    }

    Station RememberedStation(Mood target) =>
        settings.StationByMood.TryGetValue(target.Id, out var id) && target.Stations.FirstOrDefault(s => s.Id == id) is { } found
            ? found
            : target.Stations[0];

    void OnStatusChanged()
    {
        Changed(nameof(IsPlaying));
        Changed(nameof(IsBusy));
        RefreshText();
        media?.SetPlaying(Engine.IsPlaying);
    }

    void OnTrackChanged()
    {
        RefreshText();
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
            RefreshText();
            _ = LoadArtAsync();
        }
    }

    void OnMediaButton(SystemMediaTransportControlsButton button)
    {
        switch (button)
        {
            case SystemMediaTransportControlsButton.Play: Play(); break;
            case SystemMediaTransportControlsButton.Pause: Engine.Pause(); break;
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

    IcyMetadata? AudibleTrack =>
        Engine.Station == station && Engine.Metadata is { HasTrack: true, IsAdvert: false } meta ? meta : null;

    void RefreshText()
    {
        var track = AudibleTrack;
        SetText(ref title, track?.Title ?? station.Title, nameof(Title));
        SetText(ref artist, track is null ? station.Blurb : track.Artist ?? station.Title, nameof(Artist));

        string line = notice ?? Engine.Status switch
        {
            PlayerStatus.Connecting when Engine.Station == station => "Tuning in",
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
        Theme.Apply(art.Palette);
        Changed(nameof(Art));
        Changed(nameof(Cover));
        Changed(nameof(Thumb));
        ArtChanged?.Invoke();
        PushMedia();
    }

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
        artRequest?.Cancel();
        noticeTimer?.Cancel();
        Engine.Dispose();
        media?.Dispose();
        settings.Save();
    }
}
