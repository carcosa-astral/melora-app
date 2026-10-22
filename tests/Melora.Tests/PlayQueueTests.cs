using Melora.Library;
using Melora.Playback;

namespace Melora.Tests;

public class PlayQueueTests
{
    static List<Track> Library(params (string Artist, string Album, int Tracks)[] albums)
    {
        var result = new List<Track>();
        foreach (var (artist, album, count) in albums)
        {
            var group = new AlbumGroup { Title = album, Artist = artist, SampleTrackPath = $@"C:\x\{artist}\{album}\01.flac" };
            for (var i = 1; i <= count; i++)
            {
                result.Add(new Track
                {
                    Path = $@"C:\x\{artist}\{album}\{i:00}.flac",
                    Title = $"{album} {i}",
                    Artist = artist,
                    Group = group,
                    TrackNo = i,
                });
            }
        }
        return result;
    }

    static List<Track> PlayAll(PlayQueue queue, List<Track> tracks)
    {
        var played = new List<Track> { queue.Start(tracks, null)! };
        for (var i = 1; i < tracks.Count; i++) played.Add(queue.Next()!);
        return played;
    }

    [Fact]
    public void InOrder_PlaysFromTheClickedTrack()
    {
        var tracks = Library(("A", "One", 3), ("B", "Two", 2));
        var queue = new PlayQueue();

        Assert.Same(tracks[2], queue.Start(tracks, tracks[2]));
        Assert.Same(tracks[3], queue.Next());
        Assert.Same(tracks[4], queue.Next());
    }

    [Fact]
    public void Shuffle_PlaysEveryTrackOnceBeforeRepeating()
    {
        var tracks = Library(("A", "One", 10), ("B", "Two", 7), ("C", "Three", 4), ("D", "Four", 1));
        var queue = new PlayQueue();
        queue.SetShuffle(true);

        for (var run = 0; run < 50; run++)
        {
            var played = PlayAll(queue, tracks);
            Assert.Equal(tracks.Count, played.Distinct().Count());
        }
    }

    [Fact]
    public void Shuffle_NeverRepeatsAnArtistBackToBack_WhenAvoidable()
    {
        // A holds exactly half the library: the only valid orders alternate A with someone else.
        var tracks = Library(("A", "One", 3), ("B", "Two", 2), ("C", "Three", 1));
        var queue = new PlayQueue();
        queue.SetShuffle(true);

        for (var run = 0; run < 500; run++)
        {
            var played = PlayAll(queue, tracks);
            for (var i = 1; i < played.Count; i++)
                Assert.NotEqual(played[i - 1].Artist, played[i].Artist);
        }
    }

    [Fact]
    public void Shuffle_StartsAtTheClickedTrack()
    {
        var tracks = Library(("A", "One", 5), ("B", "Two", 5));
        var queue = new PlayQueue();
        queue.SetShuffle(true);

        Assert.Same(tracks[7], queue.Start(tracks, tracks[7]));
        Assert.NotEqual("B", queue.Next()!.Artist);
    }

    [Fact]
    public void RepeatOne_RepeatsUntilTheUserSkips()
    {
        var tracks = Library(("A", "One", 3));
        var queue = new PlayQueue { Repeat = RepeatMode.One };
        queue.Start(tracks, tracks[0]);

        Assert.Same(tracks[0], queue.Next());
        Assert.Same(tracks[1], queue.Next(manual: true));
    }

    [Fact]
    public void RepeatOff_StopsAtTheEnd()
    {
        var tracks = Library(("A", "One", 2));
        var queue = new PlayQueue { Repeat = RepeatMode.Off };
        queue.Start(tracks, tracks[0]);

        Assert.Same(tracks[1], queue.Next());
        Assert.Null(queue.Next());
    }
}
