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

    /// <summary>A WAV file of silence, 16-bit.</summary>
    string Wav(string name, double seconds, int rate = 44100, short channels = 2)
    {
        var path = Path.Combine(_dir, name);
        var frame = channels * 2;
        var bytes = (int)(seconds * rate) * frame;
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write(channels); w.Write(rate); w.Write(rate * frame); w.Write((short)frame); w.Write((short)16);
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

    [Theory]
    [InlineData(48000, 2)] // another sample rate
    [InlineData(44100, 1)] // mono after stereo
    public void ATrackInAnotherFormat_IsConverted_AndFollowsOn(int rate, short channels)
    {
        var (first, second) = (Wav("a.wav", 0.5), Wav("b.wav", 3, rate, channels));
        Assert.True(_engine.Play(first));
        Assert.True(_engine.SetNext(second));

        Assert.True(WaitForAdvance(TimeSpan.FromSeconds(5)));
        Assert.Equal(second, _engine.PlayingPath);
        Assert.InRange(_engine.Length, 2.9, 3.1);
        Assert.InRange(_engine.Position, 0, 1);

        // Seeking in a converted track rebuilds the output in its own format; the position holds.
        _engine.Position = 2;
        Assert.InRange(_engine.Position, 1.9, 2.3);
        Assert.Equal(second, _engine.PlayingPath);
    }

    /// <summary>Waits for something a background fade will make true (a busy test run can be slow to get to it).</summary>
    static bool Eventually(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(5)) return false;
            Thread.Sleep(20);
        }
        return true;
    }

    /// <summary>The position stays put for a moment, i.e. the fade-out has ended in a pause.</summary>
    bool Holds()
    {
        var before = _engine.Position;
        Thread.Sleep(150);
        return Math.Abs(_engine.Position - before) < 0.005;
    }

    [Fact]
    public void Pause_FadesOut_ThenHolds_AndResumeCarriesOn()
    {
        Assert.True(_engine.Play(Wav("a.wav", 5)));
        Thread.Sleep(300);

        _engine.Pause();
        Assert.False(_engine.IsPlaying); // straight away, while it fades
        Assert.True(Eventually(Holds));
        var held = _engine.Position;

        _engine.Resume();
        Assert.True(_engine.IsPlaying);
        Assert.True(Eventually(() => _engine.Position > held + 0.1));
    }

    [Fact]
    public void ResumingDuringTheFade_KeepsPlaying()
    {
        Assert.True(_engine.Play(Wav("a.wav", 5)));
        _engine.Pause();
        _engine.Resume();
        Thread.Sleep(400); // past the fade, which must not pause it now
        Assert.True(_engine.IsPlaying);
        Assert.False(Holds());
    }

    [Fact]
    public void FadeOutAndStop_CountsAsStopped_AtOnce()
    {
        Assert.True(_engine.Play(Wav("a.wav", 5)));
        _engine.FadeOutAndStop();
        Assert.False(_engine.IsLoaded);
        Assert.False(_engine.IsPlaying);
        Assert.True(Eventually(() => _engine.PlayingPath is null)); // freed once faded
    }
}
