using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Melora.Library;

namespace Melora.Discord;

/// <summary>
/// What to find a picture for: an album, a single song (radio), or with neither, the artist
/// themself (a DJ, for mixes). For an album, a file from it can be given, whose MusicBrainz release
/// ID (if tagged) finds the exact cover.
/// </summary>
public sealed record CoverQuery(string Artist, string? Album, string? Title, string? SamplePath = null)
{
    public bool IsArtist => Album is null && Title is null;
}

/// <summary>
/// Finds a web address for a cover, since Discord can only show pictures from the web, not files on
/// this PC. Tries the Cover Art Archive (only for files tagged with a MusicBrainz release), then
/// iTunes, then Deezer; artist photos come from Deezer only. Answers are kept in the database: a
/// found cover for good, a miss for a week.
/// </summary>
public sealed partial class CoverLookup(HttpClient http, LibraryDb db)
{
    static readonly TimeSpan RetryMissesAfter = TimeSpan.FromDays(7);
    const int ITunesSize = 600;

    public async Task<string?> FindAsync(CoverQuery query)
    {
        var key = Key(query);
        var (found, cachedUrl, checkedAt) = db.GetCover(key);
        if (found && (cachedUrl is not null || DateTimeOffset.UtcNow - checkedAt < RetryMissesAfter)) return cachedUrl;

        string? url = null;
        var unreachable = false;
        foreach (var source in new Func<CoverQuery, Task<string?>>[] { CoverArtArchiveAsync, ITunesAsync, DeezerAsync })
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
        query.IsArtist ? $"artist|{Normalize(query.Artist)}"
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

        foreach (var result in json?["results"]?.AsArray() ?? [])
        {
            var name = result?[album ? "collectionName" : "trackName"]?.GetValue<string>();
            if (!Matches(query, result?["artistName"]?.GetValue<string>(), name)) continue;
            if (result?["artworkUrl100"]?.GetValue<string>() is { } art)
                return art.Replace("100x100bb", $"{ITunesSize}x{ITunesSize}bb");
        }
        return null;
    }

    async Task<string?> DeezerAsync(CoverQuery query)
    {
        if (query.IsArtist) return await DeezerArtistAsync(query.Artist);
        var album = query.Album is not null;
        var q = album
            ? $"artist:\"{query.Artist}\" album:\"{query.Album}\""
            : $"artist:\"{query.Artist}\" track:\"{query.Title}\"";
        var json = await GetJsonAsync($"https://api.deezer.com/search/{(album ? "album" : "track")}?q={Uri.EscapeDataString(q)}&limit=15");

        foreach (var result in json?["data"]?.AsArray() ?? [])
        {
            if (!Matches(query, result?["artist"]?["name"]?.GetValue<string>(), result?["title"]?.GetValue<string>())) continue;
            var cover = album ? result?["cover_xl"] : result?["album"]?["cover_xl"];
            if (cover?.GetValue<string>() is { Length: > 0 } url) return url;
        }
        return null;
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

    async Task<JsonNode?> GetJsonAsync(string url)
    {
        using var response = await http.GetAsync(url);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{(int)response.StatusCode} from {new Uri(url).Host}");
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        // Deezer reports quota and other errors inside a normal-looking answer.
        if (json?["error"] is not null) throw new HttpRequestException($"Error from {new Uri(url).Host}");
        return json;
    }

    // ---- Matching ----

    static bool Matches(CoverQuery query, string? artist, string? name) =>
        artist is not null && name is not null
        && SameName(query.Artist, artist)
        && SameName(query.Album ?? query.Title ?? "", name);

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

    public static string Normalize(string name)
    {
        var text = Brackets().Replace(name, " ").Replace("&", " and ").Normalize(NormalizationForm.FormD);
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
