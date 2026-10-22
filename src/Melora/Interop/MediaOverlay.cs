using System.Windows.Threading;
using Windows.Media;
using Windows.Storage.Streams;

namespace Melora.Interop;

/// <summary>
/// Windows' media overlay (the volume flyout, lock screen and media keys), via
/// SystemMediaTransportControls attached to the main window.
/// </summary>
public sealed class MediaOverlay
{
    readonly SystemMediaTransportControls _smtc;
    int _artRequest;

    /// <summary>A transport button or media key was pressed. Raised on the UI thread.</summary>
    public event Action<SystemMediaTransportControlsButton>? ButtonPressed;

    /// <summary>The user dragged the overlay's seek bar. Raised on the UI thread.</summary>
    public event Action<TimeSpan>? SeekRequested;

    public MediaOverlay(IntPtr hwnd, Dispatcher dispatcher)
    {
        _smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsStopEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.IsPreviousEnabled = true;
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;

        _smtc.ButtonPressed += (_, e) => dispatcher.BeginInvoke(() => ButtonPressed?.Invoke(e.Button));
        _smtc.PlaybackPositionChangeRequested += (_, e) =>
            dispatcher.BeginInvoke(() => SeekRequested?.Invoke(e.RequestedPlaybackPosition));
    }

    public MediaPlaybackStatus Status
    {
        set => _smtc.PlaybackStatus = value;
    }

    /// <summary>Shows what's playing. Art is read off the UI thread and added when it's ready.</summary>
    public async void Show(string title, string artist, string? album, Func<byte[]?>? readArt)
    {
        var request = ++_artRequest;
        var updater = _smtc.DisplayUpdater;
        updater.ClearAll();
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = title;
        updater.MusicProperties.Artist = artist;
        updater.MusicProperties.AlbumTitle = album ?? "";
        updater.Update();

        if (readArt is null) return;
        try
        {
            var bytes = await Task.Run(readArt);
            if (bytes is null || request != _artRequest) return;

            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);

            if (request != _artRequest) return; // the track changed while the art was loading
            updater.Thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
            updater.Update();
        }
        catch
        {
            // No art in the overlay is fine.
        }
    }

    public void Clear()
    {
        _artRequest++;
        _smtc.DisplayUpdater.ClearAll();
        _smtc.DisplayUpdater.Update();
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
    }

    /// <summary>Position and length for the overlay's seek bar; a zero length (radio) hides it.</summary>
    public void SetTimeline(TimeSpan position, TimeSpan length)
    {
        _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            EndTime = length,
            MinSeekTime = TimeSpan.Zero,
            MaxSeekTime = length,
            Position = position,
        });
    }
}
