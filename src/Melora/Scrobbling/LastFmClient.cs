using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Melora.Library;

namespace Melora.Scrobbling;

/// <summary>
/// Last.fm's scrobbling API (2.0). Uses the user's own API key and secret; the session key from
/// the browser approval never expires, so it's stored and reused.
/// </summary>
public sealed class LastFmClient : IScrobbleService
{
    const string Endpoint = "https://ws.audioscrobbler.com/2.0/";
    const string AuthPage = "https://www.last.fm/api/auth/";

    // https://www.last.fm/api/errorcodes
    const int ErrorInvalidSession = 9;
    const int ErrorNotAuthorizedYet = 14;
    static readonly HashSet<int> RetryCodes = [8, 11, 16, 29];
    static readonly HashSet<int> AuthCodes = [4, 9, 10, 26];

    readonly HttpClient _http;
    readonly LibraryDb _db;

    public LastFmClient(HttpClient http, LibraryDb db)
    {
        _http = http;
        _db = db;
    }

    public string Id => "lastfm";
    public int MaxBatch => 50;

    public string? ApiKey => _db.GetSetting("lastfm.apiKey");
    public string? Secret => _db.GetSetting("lastfm.secret");
    string? SessionKey => _db.GetSetting("lastfm.session");
    public string? UserName => _db.GetSetting("lastfm.user");

    public bool IsConnected => ApiKey is not null && Secret is not null && SessionKey is not null;

    /// <summary>True when Last.fm rejected the saved login; the user needs to connect again.</summary>
    public bool NeedsReconnect => _db.GetSetting("lastfm.reconnect") == "1";

    // ---- Connecting ----

    /// <summary>Saves the key and secret, gets a request token, and returns the page where the user approves Melora.</summary>
    public async Task<(string Token, string Url)> BeginConnectAsync(string apiKey, string secret)
    {
        _db.SetSetting("lastfm.apiKey", apiKey.Trim());
        _db.SetSetting("lastfm.secret", secret.Trim());

        var response = await CallAsync(HttpMethod.Get, new() { ["method"] = "auth.getToken" });
        var token = response["token"]?.GetValue<string>() ?? throw new LastFmException(0, "Last.fm didn't return a token.");
        return (token, $"{AuthPage}?api_key={Uri.EscapeDataString(apiKey.Trim())}&token={Uri.EscapeDataString(token)}");
    }

    /// <summary>
    /// Asks whether the user has approved Melora yet. Returns false while they haven't;
    /// once they have, stores the session and returns true.
    /// </summary>
    public async Task<bool> TryFinishConnectAsync(string token)
    {
        try
        {
            var response = await CallAsync(HttpMethod.Get, new() { ["method"] = "auth.getSession", ["token"] = token });
            var session = response["session"] ?? throw new LastFmException(0, "Last.fm didn't return a session.");
            _db.SetSetting("lastfm.session", session["key"]!.GetValue<string>());
            _db.SetSetting("lastfm.user", session["name"]!.GetValue<string>());
            _db.SetSetting("lastfm.reconnect", "0");
            return true;
        }
        catch (LastFmException e) when (e.Code == ErrorNotAuthorizedYet)
        {
            return false;
        }
    }

    public void Disconnect()
    {
        _db.DeleteSetting("lastfm.session");
        _db.DeleteSetting("lastfm.user");
        _db.SetSetting("lastfm.reconnect", "0");
    }

    // ---- Scrobbling ----

    public async Task NowPlayingAsync(Scrobble play)
    {
        if (!IsConnected) return;
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["method"] = "track.updateNowPlaying",
            ["sk"] = SessionKey!,
        };
        AddPlay(parameters, play, index: null);
        try
        {
            await CallAsync(HttpMethod.Post, parameters);
        }
        catch
        {
            // "Now playing" is best effort.
        }
    }

    public async Task<SubmitOutcome> SubmitAsync(IReadOnlyList<Scrobble> plays)
    {
        if (!IsConnected) return SubmitOutcome.AuthFailed;

        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["method"] = "track.scrobble",
            ["sk"] = SessionKey!,
        };
        for (var i = 0; i < plays.Count; i++) AddPlay(parameters, plays[i], i);

        try
        {
            await CallAsync(HttpMethod.Post, parameters);
            return SubmitOutcome.Sent;
        }
        catch (LastFmException e)
        {
            var outcome = Classify(e.Code);
            if (outcome == SubmitOutcome.AuthFailed) _db.SetSetting("lastfm.reconnect", "1");
            return outcome;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return SubmitOutcome.RetryLater;
        }
    }

    /// <summary>What to do with queued plays after Last.fm returned an error code.</summary>
    public static SubmitOutcome Classify(int errorCode) =>
        RetryCodes.Contains(errorCode) ? SubmitOutcome.RetryLater
        : AuthCodes.Contains(errorCode) ? SubmitOutcome.AuthFailed
        : SubmitOutcome.Rejected;

    /// <summary>Adds a play's fields; batches use indexed names like <c>artist[0]</c>.</summary>
    public static void AddPlay(IDictionary<string, string> parameters, Scrobble play, int? index)
    {
        string Key(string name) => index is { } i ? $"{name}[{i}]" : name;

        parameters[Key("artist")] = play.Artist;
        parameters[Key("track")] = play.Title;
        if (index is not null) parameters[Key("timestamp")] = play.StartedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        if (play.Album is { } album) parameters[Key("album")] = album;
        if (play.AlbumArtist is { } albumArtist && albumArtist != play.Artist) parameters[Key("albumArtist")] = albumArtist;
        if (play.TrackNo > 0) parameters[Key("trackNumber")] = play.TrackNo.ToString(CultureInfo.InvariantCulture);
        if (play.Duration > TimeSpan.Zero) parameters[Key("duration")] = ((int)play.Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The text Last.fm's signature is the MD5 of: every parameter (except format and callback)
    /// as name+value, sorted by name, followed by the shared secret.
    /// </summary>
    public static string SignatureBase(IEnumerable<KeyValuePair<string, string>> parameters, string secret)
    {
        var text = new StringBuilder();
        foreach (var (name, value) in parameters
                     .Where(p => p.Key is not ("format" or "callback"))
                     .OrderBy(p => p.Key, StringComparer.Ordinal))
            text.Append(name).Append(value);
        return text.Append(secret).ToString();
    }

    public static string Sign(IEnumerable<KeyValuePair<string, string>> parameters, string secret) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(SignatureBase(parameters, secret))));

    async Task<JsonNode> CallAsync(HttpMethod method, SortedDictionary<string, string> parameters)
    {
        parameters["api_key"] = ApiKey ?? throw new LastFmException(10, "No Last.fm API key.");
        parameters["api_sig"] = Sign(parameters, Secret ?? throw new LastFmException(10, "No Last.fm shared secret."));
        parameters["format"] = "json";

        using var request = method == HttpMethod.Get
            ? new HttpRequestMessage(HttpMethod.Get, Endpoint + "?" + string.Join("&",
                parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}")))
            : new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new FormUrlEncodedContent(parameters) };

        using var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        // Errors come back as JSON with an HTTP error status, so read the body either way.
        JsonNode? json;
        try { json = JsonNode.Parse(body); }
        catch { json = null; }

        if (json?["error"] is { } error)
            throw new LastFmException(error.GetValue<int>(), json["message"]?.GetValue<string>() ?? "Last.fm error");
        if (!response.IsSuccessStatusCode || json is null)
            throw new HttpRequestException($"Last.fm answered {(int)response.StatusCode}.");
        return json;
    }
}

public sealed class LastFmException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
