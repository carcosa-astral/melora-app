using Melora.Library;

namespace Melora.Playback;

public enum RepeatMode { Off, All, One }

/// <summary>
/// The play order for the current view. There are no playlists: double-clicking a track
/// plays the list it's in, either in order or shuffled.
/// </summary>
public sealed class PlayQueue
{
    readonly Random _rng = new();
    List<Track> _source = [];
    List<Track> _order = [];
    int _index = -1;

    public bool Shuffle { get; private set; }
    public RepeatMode Repeat { get; set; } = RepeatMode.All;

    public Track? Current => _index >= 0 && _index < _order.Count ? _order[_index] : null;

    /// <summary>Starts a new run over <paramref name="tracks"/>, beginning at <paramref name="first"/> (or a random track).</summary>
    public Track? Start(IReadOnlyList<Track> tracks, Track? first)
    {
        _source = tracks.ToList();
        if (_source.Count == 0) { _order = []; _index = -1; return null; }

        first ??= Shuffle ? _source[_rng.Next(_source.Count)] : _source[0];
        Rebuild(first);
        return Current;
    }

    public void SetShuffle(bool shuffle)
    {
        Shuffle = shuffle;
        if (Current is { } current) Rebuild(current);
    }

    /// <param name="manual">True when the user pressed Next, which skips even in repeat-one.</param>
    public Track? Next(bool manual = false)
    {
        if (_order.Count == 0) return null;
        if (Repeat == RepeatMode.One && !manual) return Current;

        if (_index + 1 < _order.Count) return _order[++_index];
        if (Repeat == RepeatMode.Off) return null;

        // Wrapped around: a shuffled run gets a fresh order.
        if (Shuffle)
        {
            var last = Current;
            _order = SpreadShuffle(_source, last);
            _index = 0;
        }
        else
        {
            _index = 0;
        }
        return Current;
    }

    public Track? Previous()
    {
        if (_order.Count == 0) return null;
        if (_index > 0) _index--;
        return Current;
    }

    void Rebuild(Track current)
    {
        if (Shuffle)
        {
            _order = [current, .. SpreadShuffle(_source.Where(t => t != current), current)];
            _index = 0;
        }
        else
        {
            _order = _source;
            _index = Math.Max(0, _order.IndexOf(current));
        }
    }

    /// <summary>
    /// A random order with no repeats, where the same artist or album never plays twice in a row
    /// unless the library leaves no other choice.
    /// </summary>
    /// <remarks>
    /// Each step picks an artist at random, weighted by how many of their tracks are left, then one of
    /// their tracks. Picking only from "anyone but the last artist" without weighting drains small
    /// artists first and leaves big ones clumped at the end, so an artist holding more than half of
    /// what's left is picked straight away.
    /// </remarks>
    List<Track> SpreadShuffle(IEnumerable<Track> tracks, Track? previous)
    {
        var byArtist = tracks
            .GroupBy(t => t.Artist, StringComparer.OrdinalIgnoreCase)
            .Select(g => Shuffled(g))
            .ToList();
        var remaining = byArtist.Sum(a => a.Count);
        var result = new List<Track>(remaining);

        while (remaining > 0)
        {
            var allowed = byArtist.Where(a => previous is null || !SameArtist(a[0], previous)).ToList();
            if (allowed.Count == 0) allowed = byArtist; // only the last artist is left

            var dominant = allowed.MaxBy(a => a.Count)!;
            var artist = dominant.Count * 2 > remaining ? dominant : WeightedPick(allowed);

            // Within the artist, avoid the album that just played (matters for compilations).
            var index = previous is null ? 0 : Math.Max(0, artist.FindIndex(t => t.Group != previous.Group));
            previous = artist[index];
            artist.RemoveAt(index);
            if (artist.Count == 0) byArtist.Remove(artist);

            result.Add(previous);
            remaining--;
        }
        return result;
    }

    List<Track> WeightedPick(List<List<Track>> artists)
    {
        var total = artists.Sum(a => a.Count);
        var roll = _rng.Next(total);
        foreach (var a in artists)
        {
            if (roll < a.Count) return a;
            roll -= a.Count;
        }
        return artists[^1];
    }

    List<Track> Shuffled(IEnumerable<Track> tracks)
    {
        var list = tracks.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    static bool SameArtist(Track a, Track b) => string.Equals(a.Artist, b.Artist, StringComparison.OrdinalIgnoreCase);
}
