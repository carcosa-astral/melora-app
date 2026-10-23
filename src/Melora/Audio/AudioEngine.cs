using System.IO;
using ManagedBass;
using Melora.Radio;

namespace Melora.Audio;

public enum StreamResult { Ok, Failed, Superseded }

/// <summary>
/// Thin wrapper over BASS: one file or live URL at a time, plus format info for the status bar.
/// </summary>
/// <remarks>
/// Files don't play directly. Each one is opened as a decoder, and a single output stream pulls
/// samples from it. When a decoder runs dry and the next track has been queued with
/// <see cref="SetNext"/>, the output carries straight on into it on the very next sample, which is
/// what makes playback gapless. The output keeps a list of where in its own stream each track
/// starts, so the position and "now playing" follow what you hear rather than what's been decoded.
/// </remarks>
public sealed class AudioEngine : IDisposable
{
    // bass_aac decodes AAC itself; without it BASS falls back to Media Foundation,
    // which takes ~50 s to start an AAC radio stream. basshls plays HLS (.m3u8) radio.
    static readonly string[] Plugins = ["bassflac.dll", "bassopus.dll", "bass_aac.dll", "basshls.dll"];

    // BASS_CONFIG_DEV_DEFAULT, which ManagedBass 3.1 has no name for: follow Windows when the
    // default output device changes (headphones plugged in, a different device picked).
    const Configuration FollowDefaultDevice = (Configuration)36;

    /// <summary>A track feeding the output: its decoder and where it begins in the output stream.</summary>
    sealed class Segment(int decoder, string path)
    {
        public int Decoder { get; } = decoder;
        public string Path { get; } = path;
        /// <summary>Output byte at which this track starts.</summary>
        public long OutputStart { get; set; }
        /// <summary>Decoder byte playing at <see cref="OutputStart"/> (non-zero after a seek).</summary>
        public long DecoderStart { get; set; }
    }

    readonly SyncProcedure _onEnd;
    readonly SyncProcedure _onMeta;
    readonly StreamProcedure _fill;

    // Guards the segment list, the queued track and the byte count, which the output's fill
    // callback uses on a BASS thread. Never call BASS on the output while holding it.
    readonly object _lock = new();
    readonly List<Segment> _segments = []; // [0] is what's heard; the fill callback reads the last one
    Segment? _next;
    long _written;
    bool _drained;

    int _stream; // what's audible: the file output stream, or the live stream
    int _frequency, _channels;
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
        _fill = Fill;
    }

    public void Initialize()
    {
        // Must be set before Init; with it, device -1 means "whatever Windows' default is right now".
        Bass.Configure(FollowDefaultDevice, true);

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

    // ---- Files ----

    /// <summary>Starts playing a file. Returns false if BASS can't open it.</summary>
    public bool Play(string path)
    {
        Generation++;
        FreeStream();

        var decoder = Bass.CreateStream(path, 0, 0, BassFlags.Decode | BassFlags.Float);
        if (decoder == 0) return false;
        Bass.ChannelGetInfo(decoder, out var info);
        (_frequency, _channels) = (info.Frequency, info.Channels);

        lock (_lock)
        {
            _segments.Add(new Segment(decoder, path));
            _written = 0;
            _drained = false;
        }
        if (!CreateOutput()) return false;
        return Bass.ChannelPlay(_stream);
    }

    /// <summary>
    /// Queues the file to play straight after the current one, without a gap; null clears it.
    /// Only a file in the same sample rate and channel layout can follow on (the output can't
    /// change format mid-stream); returns false for anything else, which then starts normally
    /// once the current track has ended.
    /// </summary>
    public bool SetNext(string? path)
    {
        if (_stream == 0 || IsLive) return false;

        Segment? next = null;
        if (path is not null)
        {
            var decoder = Bass.CreateStream(path, 0, 0, BassFlags.Decode | BassFlags.Float);
            if (decoder != 0 && Bass.ChannelGetInfo(decoder, out var info)
                             && info.Frequency == _frequency && info.Channels == _channels)
                next = new Segment(decoder, path);
            else if (decoder != 0)
                Bass.StreamFree(decoder);
        }

        Segment? replaced;
        lock (_lock)
        {
            // Too late once the output has already finished with the last track.
            if (_drained && next is not null)
            {
                Bass.StreamFree(next.Decoder);
                next = null;
            }
            replaced = _next;
            _next = next;
        }
        if (replaced is not null) Bass.StreamFree(replaced.Decoder);
        return next is not null;
    }

    /// <summary>
    /// Call regularly (the UI timer does): returns true once playback has moved on into the queued
    /// track, which from then on is the current one.
    /// </summary>
    public bool CheckAdvanced()
    {
        if (_stream == 0 || IsLive) return false;
        var heard = Bass.ChannelGetPosition(_stream);

        Segment? finished = null;
        lock (_lock)
        {
            if (_segments.Count > 1 && heard >= _segments[1].OutputStart)
            {
                finished = _segments[0];
                _segments.RemoveAt(0);
            }
        }
        if (finished is null) return false;
        Bass.StreamFree(finished.Decoder);
        return true;
    }

    /// <summary>The file being heard right now.</summary>
    public string? PlayingPath
    {
        get { lock (_lock) return _segments.Count > 0 ? _segments[0].Path : null; }
    }

    /// <summary>The output's fill callback (BASS thread): copies from the decoders, moving on to the next track when one runs dry.</summary>
    int Fill(int handle, IntPtr buffer, int length, IntPtr user)
    {
        lock (_lock)
        {
            var done = 0;
            while (done < length && _segments.Count > 0)
            {
                var got = Bass.ChannelGetData(_segments[^1].Decoder, buffer + done, length - done);
                if (got > 0)
                {
                    done += got;
                    continue;
                }

                // This track is used up: carry on into the queued one, or finish.
                if (_next is null)
                {
                    _drained = true;
                    break;
                }
                _next.OutputStart = _written + done;
                _segments.Add(_next);
                _next = null;
            }

            _written += done;
            return _drained ? done | (int)StreamProcedureType.End : done;
        }
    }

    bool CreateOutput()
    {
        _stream = Bass.CreateStream(_frequency, _channels, BassFlags.Float, _fill, IntPtr.Zero);
        if (_stream == 0) return false;
        Bass.ChannelSetSync(_stream, SyncFlags.End, 0, _onEnd);
        Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, _volume);
        return true;
    }

    /// <summary>The decoder of the track being heard, or 0.</summary>
    int CurrentDecoder
    {
        get { lock (_lock) return _segments.Count > 0 ? _segments[0].Decoder : 0; }
    }

    // ---- Live streams ----

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

    // ---- Both ----

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
        get
        {
            if (_stream == 0) return 0;
            if (IsLive) return Bass.ChannelBytes2Seconds(_stream, Bass.ChannelGetPosition(_stream));

            var heard = Bass.ChannelGetPosition(_stream);
            lock (_lock)
            {
                if (_segments.Count == 0) return 0;
                var current = _segments[0];
                var bytes = current.DecoderStart + Math.Max(0, heard - current.OutputStart);
                return Bass.ChannelBytes2Seconds(current.Decoder, bytes);
            }
        }
        set
        {
            if (_stream == 0 || IsLive) return;
            Seek(Math.Max(0, value));
        }
    }

    /// <summary>
    /// Jumps within the track being heard. A user stream can't seek, so the output is rebuilt
    /// (dropping whatever it had buffered) and the queued track is reopened from its start.
    /// </summary>
    void Seek(double seconds)
    {
        var wasPlaying = IsPlaying;
        Bass.StreamFree(_stream); // also stops the fill callback
        _stream = 0;

        string? requeue = null;
        lock (_lock)
        {
            var current = _segments[0];
            foreach (var later in _segments.Skip(1))
            {
                requeue ??= later.Path;
                Bass.StreamFree(later.Decoder);
            }
            _segments.RemoveRange(1, _segments.Count - 1);
            if (_next is not null)
            {
                requeue = _next.Path;
                Bass.StreamFree(_next.Decoder);
                _next = null;
            }

            var target = Bass.ChannelSeconds2Bytes(current.Decoder, seconds);
            Bass.ChannelSetPosition(current.Decoder, target);
            current.DecoderStart = Bass.ChannelGetPosition(current.Decoder);
            current.OutputStart = 0;
            _written = 0;
            _drained = false;
        }

        if (!CreateOutput()) return;
        if (requeue is not null) SetNext(requeue);
        if (wasPlaying) Bass.ChannelPlay(_stream);
    }

    /// <summary>Length in seconds; 0 for live streams.</summary>
    public double Length
    {
        get
        {
            if (_stream == 0 || IsLive) return 0;
            var decoder = CurrentDecoder;
            return decoder == 0 ? 0 : Bass.ChannelBytes2Seconds(decoder, Bass.ChannelGetLength(decoder));
        }
    }

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

    /// <summary>Codec name, sample rate (Hz), channel count and bitrate (kbps) of what's playing.</summary>
    public (string Codec, int SampleRate, int Channels, int Kbps)? Format
    {
        get
        {
            // For files, the format is the decoder's; the output is just plain float samples.
            var handle = IsLive ? _stream : CurrentDecoder;
            if (handle == 0 || !Bass.ChannelGetInfo(handle, out var info)) return null;
            var kbps = (int)Math.Round(Bass.ChannelGetAttribute(handle, ChannelAttribute.Bitrate));

            if (kbps <= 0 && IsLive)
            {
                // Radio servers advertise their bitrate in the icy-br header.
                var headers = ReadTags(_stream, TagType.ICY).Concat(ReadTags(_stream, TagType.HTTP));
                _ = int.TryParse(StreamMetadata.HeaderValue(headers, "icy-br")?.Split(',')[0], out kbps);
            }
            else if (kbps <= 0 && PlayingPath is { } path && Length > 0)
            {
                // BASSOPUS doesn't report a bitrate; fall back to the file's average.
                kbps = (int)Math.Round(new FileInfo(path).Length * 8 / Length / 1000);
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
        if (_stream != 0) Bass.StreamFree(_stream); // first, so the fill callback has stopped
        _stream = 0;
        IsLive = false;

        List<int> decoders;
        lock (_lock)
        {
            decoders = _segments.Select(s => s.Decoder).ToList();
            if (_next is not null) decoders.Add(_next.Decoder);
            _segments.Clear();
            _next = null;
            _written = 0;
            _drained = false;
        }
        foreach (var decoder in decoders) Bass.StreamFree(decoder);
    }

    public void Dispose()
    {
        FreeStream();
        Bass.Free();
    }
}
