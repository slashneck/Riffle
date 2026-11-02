using Windows.Media;
using Windows.Storage.Streams;

namespace Riffle.Services;

// Hooks into Windows' own media controls: the now playing tile in the volume flyout,
// the lock screen, and the keyboard media keys.
public sealed class MediaControls : IDisposable
{
    readonly SystemMediaTransportControls controls;
    readonly SynchronizationContext ui;
    int version;
    MediaPlaybackStatus status = MediaPlaybackStatus.Closed;
    (string, string, string, byte[]?) shown;

    public MediaControls(IntPtr window, SynchronizationContext ui)
    {
        this.ui = ui;
        controls = SystemMediaTransportControlsInterop.GetForWindow(window);
        controls.IsEnabled = true;
        controls.IsPlayEnabled = true;
        controls.IsPauseEnabled = true;
        controls.IsNextEnabled = true;
        controls.IsPreviousEnabled = true;
        controls.PlaybackStatus = MediaPlaybackStatus.Closed;
        controls.ButtonPressed += OnButtonPressed;
    }

    public event Action<SystemMediaTransportControlsButton>? ButtonPressed;

    void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        var button = args.Button;
        ui.Post(_ => ButtonPressed?.Invoke(button), null);
    }

    public void SetPlaying(bool playing)
    {
        status = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        controls.PlaybackStatus = status;
    }

    public async void Show(string title, string artist, string album, byte[]? cover)
    {
        if (shown == (title, artist, album, cover))
            return;
        shown = (title, artist, album, cover);
        int mine = ++version;
        try
        {
            RandomAccessStreamReference? thumbnail = null;
            if (cover is { Length: > 0 })
            {
                var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(cover);
                    await writer.StoreAsync();
                    writer.DetachStream();
                }
                stream.Seek(0);
                thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
            }

            if (mine != version)
                return;

            var display = controls.DisplayUpdater;
            display.ClearAll();
            display.Type = MediaPlaybackType.Music;
            display.MusicProperties.Title = title;
            display.MusicProperties.Artist = artist;
            display.MusicProperties.AlbumTitle = album;
            display.Thumbnail = thumbnail;
            display.Update();

            // clearing the display also forgets the play state
            controls.PlaybackStatus = status;
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        controls.ButtonPressed -= OnButtonPressed;
        try
        {
            controls.DisplayUpdater.ClearAll();
            controls.DisplayUpdater.Update();
            controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            controls.IsEnabled = false;
        }
        catch (Exception)
        {
        }
    }
}
