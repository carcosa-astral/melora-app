using System.Diagnostics;
using System.IO;
using Melora.Audio;

namespace Melora.Tests;

/// <summary>
/// The gapless hand-over and seeking, on BASS's "no sound" device: it plays in real time like a
/// sound card, so no audio hardware is needed (GitHub's build machines have none).
/// </summary>
public sealed class AudioEngineTests(AudioEngineTests.Engine shared) : IClassFixture<AudioEngineTests.Engine>, IDisposable
{
    /// <summary>BASS starts once per process (its plugins stay loaded), so the tests share one engine.</summary>
    public sealed class Engine : IDisposable
    {
        public AudioEngine Audio { get; } = new();
        public Engine() => Audio.Initialize(device: 0);
        public void Dispose() => Audio.Dispose();
    }

    readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"melora-audio-{Guid.NewGuid():N}")).FullName;
    readonly AudioEngine _engine = shared.Audio;

    public void Dispose()
    {
        _engine.Stop(); // lets go of the files
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A WAV file of silence, 16-bit stereo.</summary>
    string Wav(string name, double seconds, int rate = 44100)
    {
        var path = Path.Combine(_dir, name);
        var bytes = (int)(seconds * rate) * 4;
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes); w.Write(new byte[bytes]);
        return path;
    }

    /// <summary>Polls the way the UI timer does, until playback moves into the queued track.</summary>
    bool WaitForAdvance(TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < limit)
        {
            if (_engine.CheckAdvanced()) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    [Fact]
    public void QueuedTrack_TakesOver_WhenTheFirstEnds()
    {
        var (first, second) = (Wav("a.wav", 0.5), Wav("b.wav", 2));
        Assert.True(_engine.Play(first));
        Assert.True(_engine.SetNext(second));

        Assert.True(WaitForAdvance(TimeSpan.FromSeconds(5)));
        Assert.Equal(second, _engine.PlayingPath);
        Assert.InRange(_engine.Length, 1.9, 2.1);
        Assert.InRange(_engine.Position, 0, 1); // counted from the second track's start, not the first's
    }

    [Fact]
    public void SeekingNearTheEnd_KeepsTheNextTrackQueued()
    {
        var (first, second) = (Wav("a.wav", 3), Wav("b.wav", 2));
        Assert.True(_engine.Play(first));
        Assert.True(_engine.SetNext(second));

        _engine.Position = 2.6;
        Assert.InRange(_engine.Position, 2.5, 2.8);

        Assert.True(WaitForAdvance(TimeSpan.FromSeconds(5)));
        Assert.Equal(second, _engine.PlayingPath);
    }

    [Fact]
    public void ADifferentSampleRate_IsNotQueued()
    {
        Assert.True(_engine.Play(Wav("a.wav", 1)));
        Assert.False(_engine.SetNext(Wav("b.wav", 1, rate: 48000)));
    }
}
