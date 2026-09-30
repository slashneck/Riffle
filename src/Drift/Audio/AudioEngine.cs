using Drift.Radio;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Drift.Audio;

public enum PlayerStatus { Idle, Connecting, Playing, Buffering, Paused, Error }

public sealed class AudioEngine : IDisposable
{
    const double FirstFade = 2.5;
    const double Crossfade = 4.0;
    const double PauseFade = 0.6;

    readonly SynchronizationContext ui;
    readonly Mixer mixer = new();
    readonly MMDeviceEnumerator devices = new();
    readonly DeviceWatcher watcher;
    readonly List<Deck> outgoing = [];
    WasapiOut? output;
    Deck? current;
    bool playing;

    public AudioEngine(SynchronizationContext ui)
    {
        this.ui = ui;
        Spectrum = new Spectrum(mixer);
        mixer.DeckSilent += deck => ui.Post(_ => Retire(deck), null);

        watcher = new DeviceWatcher(() => ui.Post(_ => RestartOutput(), null));
        devices.RegisterEndpointNotificationCallback(watcher);
    }

    public Spectrum Spectrum { get; }
    public Station? Station { get; private set; }
    public bool IsPlaying => playing;
    public PlayerStatus Status { get; private set; }
    public IcyMetadata? Metadata => current is { HasStarted: true } deck ? deck.Metadata : null;

    public float Volume
    {
        get => mixer.TargetVolume;
        set => mixer.TargetVolume = Math.Clamp(value, 0f, 1f);
    }

    public event Action? StatusChanged;
    public event Action? TrackChanged;
    public event Action<Station>? StationFailed;
    public event Action? StationRestored;

    public void Play(Station station)
    {
        bool wasPlaying = playing;
        playing = true;
        EnsureOutput();

        if (current is not null && current.Station == station && current.State != DeckState.Failed)
        {
            if (current.HasStarted)
                current.FadeTo(1f, wasPlaying ? 0.3 : 0.8);
            SetStatus(MapState(current.State));
            return;
        }

        Station = station;
        if (current is not null)
        {
            // the old station keeps playing until the new one has buffered
            if (current.HasStarted)
                outgoing.Add(current);
            else
                Drop(current);
        }

        var deck = new Deck(station, ui);
        deck.Ready += OnReady;
        deck.StateChanged += OnDeckState;
        deck.MetadataChanged += OnDeckMetadata;
        current = deck;
        mixer.Add(deck);
        deck.Start();
        SetStatus(PlayerStatus.Connecting);
    }

    public void Pause()
    {
        if (!playing)
            return;

        playing = false;
        current?.FadeTo(0f, PauseFade);
        foreach (var deck in outgoing)
            deck.FadeTo(0f, PauseFade);
        SetStatus(PlayerStatus.Paused);

        if (output is null)
            StopAll();
    }

    void OnReady(Deck deck)
    {
        if (deck != current)
            return;

        if (!playing)
        {
            deck.FadeTo(0f, 0);
            return;
        }

        foreach (var old in outgoing)
            old.FadeTo(0f, Crossfade);
        deck.FadeTo(1f, outgoing.Count > 0 ? Crossfade : FirstFade);

        SetStatus(PlayerStatus.Playing);
        TrackChanged?.Invoke();
    }

    void OnDeckState(Deck deck)
    {
        if (deck != current)
            return;

        if (deck.State != DeckState.Failed)
        {
            if (playing)
                SetStatus(MapState(deck.State));
            return;
        }

        var failed = deck.Station;
        Drop(deck);
        current = null;

        if (outgoing.Count > 0)
        {
            // stay on whatever was playing before instead of dropping into silence
            current = outgoing[^1];
            outgoing.RemoveAt(outgoing.Count - 1);
            Station = current.Station;
            current.FadeTo(1f, 0.5);
            StationFailed?.Invoke(failed);
            StationRestored?.Invoke();
            SetStatus(MapState(current.State));
            TrackChanged?.Invoke();
            return;
        }

        playing = false;
        StationFailed?.Invoke(failed);
        SetStatus(PlayerStatus.Error);
        StopOutputIfIdle();
    }

    void OnDeckMetadata(Deck deck)
    {
        if (deck == current && deck.HasStarted)
            TrackChanged?.Invoke();
    }

    void Retire(Deck deck)
    {
        if (deck == current)
        {
            if (playing)
                return;
            Drop(deck);
            current = null;
        }
        else if (outgoing.Remove(deck))
        {
            Drop(deck);
        }
        StopOutputIfIdle();
    }

    void Drop(Deck deck)
    {
        deck.Ready -= OnReady;
        deck.StateChanged -= OnDeckState;
        deck.MetadataChanged -= OnDeckMetadata;
        mixer.Remove(deck);
        deck.Dispose();
    }

    void StopAll()
    {
        if (current is not null)
            Drop(current);
        current = null;
        foreach (var deck in outgoing)
            Drop(deck);
        outgoing.Clear();
        StopOutputIfIdle();
    }

    void StopOutputIfIdle()
    {
        if (playing || current is not null || outgoing.Count > 0)
            return;
        output?.Stop();
        mixer.ClearRecent();
    }

    void EnsureOutput()
    {
        if (output is not null)
        {
            if (output.PlaybackState != PlaybackState.Playing)
                output.Play();
            return;
        }

        try
        {
            var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var wasapi = new WasapiOut(device, AudioClientShareMode.Shared, true, 150);
            try
            {
                wasapi.Init(mixer);
            }
            catch (Exception)
            {
                int rate = device.AudioClient.MixFormat.SampleRate;
                wasapi.Dispose();
                wasapi = new WasapiOut(device, AudioClientShareMode.Shared, true, 150);
                wasapi.Init(new WdlResamplingSampleProvider(mixer, rate));
            }
            wasapi.PlaybackStopped += OnPlaybackStopped;
            output = wasapi;
            wasapi.Play();
        }
        catch (Exception)
        {
            output = null;
        }
    }

    void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            ui.Post(_ => RestartOutput(), null);
    }

    void RestartOutput()
    {
        var old = output;
        output = null;
        if (old is not null)
        {
            old.PlaybackStopped -= OnPlaybackStopped;
            old.Dispose();
        }
        if (playing || current is not null)
            EnsureOutput();
    }

    void SetStatus(PlayerStatus status)
    {
        if (Status == status)
            return;
        Status = status;
        StatusChanged?.Invoke();
    }

    static PlayerStatus MapState(DeckState state) => state switch
    {
        DeckState.Playing => PlayerStatus.Playing,
        DeckState.Buffering => PlayerStatus.Buffering,
        DeckState.Failed => PlayerStatus.Error,
        _ => PlayerStatus.Connecting,
    };

    public void Dispose()
    {
        playing = false;
        try { devices.UnregisterEndpointNotificationCallback(watcher); } catch (Exception) { }
        StopAll();
        output?.Dispose();
        output = null;
        devices.Dispose();
    }

    sealed class DeviceWatcher(Action defaultChanged) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia)
                defaultChanged();
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
