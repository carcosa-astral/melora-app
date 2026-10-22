using System.IO;
using ManagedBass;
using Melora.Radio;

namespace Melora.Audio;

public enum StreamResult { Ok, Failed, Superseded }

/// <summary>Thin wrapper over BASS: one stream at a time (a file or a live URL), plus format info for the status bar.</summary>
public sealed class AudioEngine : IDisposable
{
    static readonly string[] Plugins = ["bassflac.dll", "bassopus.dll"];

    readonly SyncProcedure _onEnd;
    readonly SyncProcedure _onMeta;
    int _stream;
    string? _path;
    double _volume = 1.0;

    /// <summary>
    /// Bumped every time playback switches, so callbacks and slow connections from an
    /// earlier stream can tell they're stale.
    /// </summary>
    public int Generation { get; private set; }

    /// <summary>Raised on a BASS thread when the current stream ends, with the generation it belonged to.</summary>
    public event Action<int>? Ended;

    /// <summary>Raised on a BASS thread when a live stream sends a new title.</summary>
    public event Action<int>? MetadataChanged;

    public AudioEngine()
    {
        // Keep the delegates alive for as long as BASS may call them.
        _onEnd = (_, _, _, _) => Ended?.Invoke(Generation);
        _onMeta = (_, _, _, _) => MetadataChanged?.Invoke(Generation);
    }

    public void Initialize()
    {
        if (!Bass.Init())
            throw new InvalidOperationException($"Could not start audio output ({Bass.LastError}).");

        foreach (var plugin in Plugins)
        {
            if (Bass.PluginLoad(Path.Combine(AppPaths.BaseDir, plugin)) == 0)
                throw new InvalidOperationException($"Could not load {plugin} ({Bass.LastError}).");
        }

        // Let pasted .pls / .m3u station links resolve to the stream inside them.
        Bass.Configure(Configuration.NetPlaylist, 1);
    }

    /// <summary>Starts playing a file. Returns false if BASS can't open it.</summary>
    public bool Play(string path)
    {
        Generation++;
        FreeStream();

        _stream = Bass.CreateStream(path, 0, 0, BassFlags.Float);
        if (_stream == 0) return false;
        _path = path;
        IsLive = false;

        Bass.ChannelSetSync(_stream, SyncFlags.End, 0, _onEnd);
        Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, _volume);
        return Bass.ChannelPlay(_stream);
    }

    /// <summary>
    /// Connects to an internet stream and starts playing it. Connecting happens off the UI thread;
    /// if something else starts playing meanwhile, the connection is dropped and Superseded returned.
    /// </summary>
    public async Task<StreamResult> PlayUrlAsync(string url)
    {
        var generation = ++Generation;
        FreeStream();

        var (handle, error) = await Task.Run(() =>
        {
            var h = Bass.CreateStream(url, 0, BassFlags.Float, null, IntPtr.Zero);
            return (h, Bass.LastError); // LastError is per-thread, so read it here
        });

        if (generation != Generation)
        {
            if (handle != 0) Bass.StreamFree(handle);
            return StreamResult.Superseded;
        }
        if (handle == 0)
        {
            LastError = error;
            return StreamResult.Failed;
        }

        _stream = handle;
        IsLive = true;
        Bass.ChannelSetSync(_stream, SyncFlags.End, 0, _onEnd);
        Bass.ChannelSetSync(_stream, SyncFlags.MetadataReceived, 0, _onMeta);
        Bass.ChannelSetSync(_stream, SyncFlags.OggChange, 0, _onMeta);
        Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, _volume);
        Bass.ChannelPlay(_stream);
        return StreamResult.Ok;
    }

    /// <summary>Why the last <see cref="PlayUrlAsync"/> failed.</summary>
    public Errors LastError { get; private set; }

    public bool IsLoaded => _stream != 0;

    /// <summary>True when the current stream is an internet stream rather than a file.</summary>
    public bool IsLive { get; private set; }

    public bool IsPlaying => _stream != 0 && Bass.ChannelIsActive(_stream) == PlaybackState.Playing;

    public void Pause()
    {
        if (_stream != 0) Bass.ChannelPause(_stream);
    }

    public void Resume()
    {
        if (_stream != 0) Bass.ChannelPlay(_stream);
    }

    public void Stop()
    {
        Generation++;
        FreeStream();
    }

    /// <summary>Position in seconds (for a live stream: time listened).</summary>
    public double Position
    {
        get => _stream == 0 ? 0 : Bass.ChannelBytes2Seconds(_stream, Bass.ChannelGetPosition(_stream));
        set
        {
            if (_stream != 0 && !IsLive)
                Bass.ChannelSetPosition(_stream, Bass.ChannelSeconds2Bytes(_stream, Math.Max(0, value)));
        }
    }

    /// <summary>Length in seconds; 0 for live streams.</summary>
    public double Length => _stream == 0 || IsLive ? 0 : Bass.ChannelBytes2Seconds(_stream, Bass.ChannelGetLength(_stream));

    /// <summary>Volume from 0 to 1, kept across tracks.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            if (_stream != 0) Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, _volume);
        }
    }

    /// <summary>Codec name, sample rate (Hz), channel count and bitrate (kbps) of the current stream.</summary>
    public (string Codec, int SampleRate, int Channels, int Kbps)? Format
    {
        get
        {
            if (_stream == 0 || !Bass.ChannelGetInfo(_stream, out var info)) return null;
            var kbps = (int)Math.Round(Bass.ChannelGetAttribute(_stream, ChannelAttribute.Bitrate));

            if (kbps <= 0 && IsLive)
            {
                // Radio servers advertise their bitrate in the icy-br header.
                var headers = ReadTags(_stream, TagType.ICY).Concat(ReadTags(_stream, TagType.HTTP));
                _ = int.TryParse(StreamMetadata.HeaderValue(headers, "icy-br")?.Split(',')[0], out kbps);
            }
            else if (kbps <= 0 && _path is not null && Length > 0)
            {
                // BASSOPUS doesn't report a bitrate; fall back to the file's average.
                kbps = (int)Math.Round(new FileInfo(_path).Length * 8 / Length / 1000);
            }
            return (CodecName(info.ChannelType), info.Frequency, info.Channels, kbps);
        }
    }

    /// <summary>The title a live stream says is playing now, if any.</summary>
    public string? StreamTitle
    {
        get
        {
            if (_stream == 0 || !IsLive) return null;
            var meta = Bass.ChannelGetTags(_stream, TagType.META);
            if (meta != IntPtr.Zero) return StreamMetadata.ParseIcyTitle(Extensions.PtrToStringUtf8(meta));
            return StreamMetadata.FromVorbisComments(ReadTags(_stream, TagType.OGG));
        }
    }

    public static string[] ReadTags(int handle, TagType type)
    {
        var ptr = Bass.ChannelGetTags(handle, type);
        return ptr == IntPtr.Zero ? [] : Extensions.ExtractMultiStringUtf8(ptr);
    }

    public static string CodecName(ChannelType type) => type switch
    {
        ChannelType.MP3 or ChannelType.MP2 or ChannelType.MP1 => "MP3",
        ChannelType.AAC or ChannelType.MP4 or ChannelType.MF => "AAC",
        ChannelType.OGG => "Vorbis",
        ChannelType.OPUS => "Opus",
        ChannelType.FLAC or ChannelType.FLAC_OGG => "FLAC",
        _ => type.ToString().ToUpperInvariant(),
    };

    void FreeStream()
    {
        if (_stream == 0) return;
        Bass.StreamFree(_stream);
        _stream = 0;
        _path = null;
        IsLive = false;
    }

    public void Dispose()
    {
        FreeStream();
        Bass.Free();
    }
}
