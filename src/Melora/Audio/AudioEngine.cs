using System.IO;
using ManagedBass;

namespace Melora.Audio;

/// <summary>Thin wrapper over BASS: one stream at a time, plus format info for the status bar.</summary>
public sealed class AudioEngine : IDisposable
{
    static readonly string[] Plugins = ["bassflac.dll", "bassopus.dll"];

    readonly SyncProcedure _onEnd;
    int _stream;
    string? _path;
    double _volume = 1.0;

    /// <summary>Raised on a BASS thread when the current stream reaches its end.</summary>
    public event Action? Ended;

    public AudioEngine()
    {
        // Keep the delegate alive for as long as BASS may call it.
        _onEnd = (_, _, _, _) => Ended?.Invoke();
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
    }

    /// <summary>Starts playing a file. Returns false if BASS can't open it.</summary>
    public bool Play(string path)
    {
        FreeStream();

        _stream = Bass.CreateStream(path, 0, 0, BassFlags.Float);
        if (_stream == 0) return false;
        _path = path;

        Bass.ChannelSetSync(_stream, SyncFlags.End, 0, _onEnd);
        Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, _volume);
        return Bass.ChannelPlay(_stream);
    }

    public bool IsLoaded => _stream != 0;

    public bool IsPlaying => _stream != 0 && Bass.ChannelIsActive(_stream) == PlaybackState.Playing;

    public void Pause()
    {
        if (_stream != 0) Bass.ChannelPause(_stream);
    }

    public void Resume()
    {
        if (_stream != 0) Bass.ChannelPlay(_stream);
    }

    public void Stop() => FreeStream();

    /// <summary>Position in seconds.</summary>
    public double Position
    {
        get => _stream == 0 ? 0 : Bass.ChannelBytes2Seconds(_stream, Bass.ChannelGetPosition(_stream));
        set
        {
            if (_stream != 0)
                Bass.ChannelSetPosition(_stream, Bass.ChannelSeconds2Bytes(_stream, Math.Max(0, value)));
        }
    }

    /// <summary>Length in seconds.</summary>
    public double Length => _stream == 0 ? 0 : Bass.ChannelBytes2Seconds(_stream, Bass.ChannelGetLength(_stream));

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

    /// <summary>Sample rate (Hz), channel count and bitrate (kbps) of the current stream.</summary>
    public (int SampleRate, int Channels, int Kbps)? Format
    {
        get
        {
            if (_stream == 0 || !Bass.ChannelGetInfo(_stream, out var info)) return null;
            var kbps = (int)Math.Round(Bass.ChannelGetAttribute(_stream, ChannelAttribute.Bitrate));
            // BASSOPUS doesn't report a bitrate; fall back to the file's average.
            if (kbps <= 0 && _path is not null && Length > 0)
                kbps = (int)Math.Round(new FileInfo(_path).Length * 8 / Length / 1000);
            return (info.Frequency, info.Channels, kbps);
        }
    }

    void FreeStream()
    {
        if (_stream == 0) return;
        Bass.StreamFree(_stream);
        _stream = 0;
        _path = null;
    }

    public void Dispose()
    {
        FreeStream();
        Bass.Free();
    }
}
