using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Melora.Library;

namespace Melora.Discord;

/// <summary>
/// What to find a picture for: an album, a single song (radio), a radio station's logo (by its
/// address, with its name in Artist), or with none of those, the artist themself (a DJ, for mixes).
/// For an album, a file from it can be given, whose MusicBrainz release ID (if tagged) finds the
/// exact cover.
/// </summary>
public sealed record CoverQuery(string Artist, string? Album, string? Title, string? SamplePath = null, string? StationUrl = null)
{
    public bool IsStation => StationUrl is not null;
    public bool IsArtist => Album is null && Title is null && !IsStation;
}

/// <summary>
/// Finds a web address for a cover, since Discord can only show pictures from the web, not files on
/// this PC. Tries the Cover Art Archive (only for files tagged with a MusicBrainz release), then
/// iTunes, then Deezer; artist photos come from Deezer only, and station logos from Radio Browser.
/// Answers are kept in the database: a found cover for good, a miss for a week.
/// </summary>
public sealed partial class CoverLookup(HttpClient http, LibraryDb db)
{
    static readonly TimeSpan RetryMissesAfter = TimeSpan.FromDays(7);
    const int ITunesSize = 600;

    /// <summary>A lookup with its own web client, identified as Melora.</summary>
    public static CoverLookup Create(LibraryDb db)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Melora/{UpdateCheck.CurrentVersion}");
        return new CoverLookup(http, db);
    }

    /// <summary>Downloads a found cover (for showing it in Melora itself); null if it can't be fetched.</summary>
    public async Task<byte[]?> DownloadAsync(string url)
    {
        try
        {
            return await http.GetByteArrayAsync(url);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public async Task<string?> FindAsync(CoverQuery query)
    {
        var key = Key(query);
        var (found, cachedUrl, checkedAt) = db.GetCover(key);
        if (found && (cachedUrl is not null || DateTimeOffset.UtcNow - checkedAt < RetryMissesAfter)) return cachedUrl;

        string? url = null;
        var unreachable = false;
        Func<CoverQuery, Task<string?>>[] sources = query.IsStation ? [RadioBrowserAsync] : [CoverArtArchiveAsync, ITunesAsync, DeezerAsync];
        foreach (var source in sources)
        {
            try
            {
                url = await source(query);
                if (url is not null) break;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                unreachable = true;
            }
        }

        // A miss only counts if every source actually answered; being offline isn't "no cover".
        if (url is not null || !unreachable) db.SetCover(key, url);
        return url;
    }

    public static string Key(CoverQuery query) =>
        query.IsStation ? $"station|{query.StationUrl}"
        : query.IsArtist ? $"artist|{Normalize(query.Artist)}"
        : query.Album is { } album ? $"album|{Normalize(query.Artist)}|{Normalize(album)}"
        : $"song|{Normalize(query.Artist)}|{Normalize(query.Title ?? "")}";

    // ---- Sources ----

    async Task<string?> CoverArtArchiveAsync(CoverQuery query)
    {
        if (query.SamplePath is not { } path) return null;
        var mbid = await Task.Run(() => Track.ReadTag(path, tag => tag.MusicBrainzReleaseId));
        if (mbid is null || !Guid.TryParse(mbid, out _)) return null;

        // The archive answers with a redirect to whichever archive.org server holds the image today,
        // so keep its own address, which stays put, and let Discord follow the redirect.
        var url = $"https://coverartarchive.org/release/{mbid}/front-500";
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode ? url : null;
    }

    async Task<string?> ITunesAsync(CoverQuery query)
    {
        if (query.IsArtist) return null; // the iTunes search has no artist photos
        var album = query.Album is not null;
        var term = Uri.EscapeDataString($"{query.Artist} {(album ? query.Album : query.Title)}");
        var json = await GetJsonAsync($"https://itunes.apple.com/search?term={term}&entity={(album ? "album" : "song")}&limit=15");

        var art = Pick(query, (json?["results"]?.AsArray() ?? []).Select(result => (
            result?["artistName"]?.GetValue<string>(),
            result?[album ? "collectionName" : "trackName"]?.GetValue<string>(),
            result?["artworkUrl100"]?.GetValue<string>())));
        return art?.Replace("100x100bb", $"{ITunesSize}x{ITunesSize}bb");
    }

    async Task<string?> DeezerAsync(CoverQuery query)
    {
        if (query.IsArtist) return await DeezerArtistAsync(query.Artist);
        var album = query.Album is not null;
        var q = album
            ? $"artist:\"{query.Artist}\" album:\"{query.Album}\""
            : $"artist:\"{query.Artist}\" track:\"{query.Title}\"";
        var json = await GetJsonAsync($"https://api.deezer.com/search/{(album ? "album" : "track")}?q={Uri.EscapeDataString(q)}&limit=15");

        return Pick(query, (json?["data"]?.AsArray() ?? []).Select(result => (
            result?["artist"]?["name"]?.GetValue<string>(),
            result?["title"]?.GetValue<string>(),
            (album ? result?["cover_xl"] : result?["album"]?["cover_xl"])?.GetValue<string>())));
    }

    async Task<string?> DeezerArtistAsync(string artist)
    {
        var json = await GetJsonAsync($"https://api.deezer.com/search/artist?q={Uri.EscapeDataString(artist)}&limit=10");
        foreach (var result in json?["data"]?.AsArray() ?? [])
        {
            // Only the same name exactly: a prefix match could put up a stranger's photo.
            if (Normalize(result?["name"]?.GetValue<string>() ?? "") != Normalize(artist)) continue;
            // Artists without a photo get a placeholder whose address has an empty image ID.
            if (result?["picture_xl"]?.GetValue<string>() is { Length: > 0 } url && !url.Contains("/artist//")) return url;
        }
        return null;
    }

    /// <summary>
    /// A station's logo from Radio Browser (the community station directory): the entry with the
    /// same stream address, or else the most voted-for one whose name contains the station's.
    /// </summary>
    async Task<string?> RadioBrowserAsync(CoverQuery query)
    {
        const string api = "https://all.api.radio-browser.info/json/stations/";
        string? Logo(JsonNode? station) => station?["favicon"]?.GetValue<string>() is { Length: > 0 } url ? url : null;

        var byUrl = await GetJsonAsync($"{api}byurl?url={Uri.EscapeDataString(query.StationUrl!)}");
        if ((byUrl?.AsArray() ?? []).Select(Logo).FirstOrDefault(url => url is not null) is { } exact) return exact;

        var name = Normalize(query.Artist);
        if (name.Length == 0) return null;
        var byName = await GetJsonAsync($"{api}search?name={Uri.EscapeDataString(query.Artist)}&order=votes&reverse=true&hidebroken=true&limit=20");
        return (byName?.AsArray() ?? [])
            .Where(s => Normalize(s?["name"]?.GetValue<string>() ?? "").Contains(name, StringComparison.Ordinal))
            .Select(Logo).FirstOrDefault(url => url is not null);
    }

    async Task<JsonNode?> GetJsonAsync(string url)
    {
        using var response = await http.GetAsync(url);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{(int)response.StatusCode} from {new Uri(url).Host}");
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        // Deezer reports quota and other errors inside a normal-looking answer.
        if (json is JsonObject { } obj && obj["error"] is not null) throw new HttpRequestException($"Error from {new Uri(url).Host}");
        return json;
    }

    // ---- Matching ----

    /// <summary>
    /// The picture of the best search result: one whose name matches exactly, brackets included
    /// (so "Genesis (Christopher Lobsinger Remix)" gets the remix's cover), or else the first that
    /// matches loosely (the original's cover).
    /// </summary>
    public static string? Pick(CoverQuery query, IEnumerable<(string? Artist, string? Name, string? Url)> results)
    {
        var name = query.Album ?? query.Title ?? "";
        var usable = results
            .Where(r => r is { Artist: not null, Name: not null, Url.Length: > 0 } && SameName(query.Artist, r.Artist!))
            .ToList();
        var exact = usable.FirstOrDefault(r => Normalize(r.Name!, keepBrackets: true) == Normalize(name, keepBrackets: true));
        return exact.Url ?? usable.FirstOrDefault(r => SameName(name, r.Name!)).Url;
    }

    /// <summary>
    /// Loose name comparison: ignores case, accents, punctuation, a leading "The" and anything in
    /// brackets, and accepts one name starting with the other ("Album" and "Album Deluxe Edition").
    /// </summary>
    public static bool SameName(string a, string b)
    {
        var (x, y) = (Normalize(a), Normalize(b));
        if (x.Length == 0 || y.Length == 0) return false;
        if (x == y) return true;
        var (shorter, longer) = x.Length < y.Length ? (x, y) : (y, x);
        return shorter.Length >= 4 && longer.StartsWith(shorter, StringComparison.Ordinal);
    }

    public static string Normalize(string name, bool keepBrackets = false)
    {
        var text = (keepBrackets ? name : Brackets().Replace(name, " ")).Replace("&", " and ").Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) result.Append(char.ToLowerInvariant(c));
            else if (result.Length > 0 && result[^1] != ' ') result.Append(' ');
        }
        var normal = result.ToString().Trim();
        if (normal.StartsWith("the ", StringComparison.Ordinal)) normal = normal[4..];
        return normal.Replace(" ", "");
    }

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Brackets();
}
