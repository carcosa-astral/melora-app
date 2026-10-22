using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Melora.Library;

namespace Melora.Scrobbling;

/// <summary>ListenBrainz (MusicBrainz's open scrobbling service), authorised with the user's token.</summary>
public sealed class ListenBrainzClient : IScrobbleService
{
    const string Api = "https://api.listenbrainz.org/1/";

    static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0";

    readonly HttpClient _http;
    readonly LibraryDb _db;

    public ListenBrainzClient(HttpClient http, LibraryDb db)
    {
        _http = http;
        _db = db;
    }

    public string Id => "listenbrainz";

    // The API caps a request at 1000 listens / 10 KB; 50 keeps well under the size limit.
    public int MaxBatch => 50;

    string? Token => _db.GetSetting("listenbrainz.token");
    public string? UserName => _db.GetSetting("listenbrainz.user");

    public bool IsConnected => Token is not null;

    public bool NeedsReconnect => _db.GetSetting("listenbrainz.reconnect") == "1";

    /// <summary>Checks a token with ListenBrainz and saves it if it's valid. Returns the user name, or null.</summary>
    public async Task<string?> ConnectAsync(string token)
    {
        token = token.Trim();
        using var request = new HttpRequestMessage(HttpMethod.Get, Api + "validate-token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        using var response = await _http.SendAsync(request);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        if (json?["valid"]?.GetValue<bool>() != true) return null;
        var user = json["user_name"]?.GetValue<string>() ?? "";
        _db.SetSetting("listenbrainz.token", token);
        _db.SetSetting("listenbrainz.user", user);
        _db.SetSetting("listenbrainz.reconnect", "0");
        return user;
    }

    public void Disconnect()
    {
        _db.DeleteSetting("listenbrainz.token");
        _db.DeleteSetting("listenbrainz.user");
        _db.SetSetting("listenbrainz.reconnect", "0");
    }

    public async Task NowPlayingAsync(Scrobble play)
    {
        if (!IsConnected) return;
        try
        {
            await PostAsync(BuildPayload("playing_now", [play]));
        }
        catch
        {
            // "Now playing" is best effort.
        }
    }

    public async Task<SubmitOutcome> SubmitAsync(IReadOnlyList<Scrobble> plays)
    {
        if (!IsConnected) return SubmitOutcome.AuthFailed;
        try
        {
            var status = await PostAsync(BuildPayload(plays.Count == 1 ? "single" : "import", plays));
            var outcome = Classify(status);
            if (outcome == SubmitOutcome.AuthFailed) _db.SetSetting("listenbrainz.reconnect", "1");
            return outcome;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return SubmitOutcome.RetryLater;
        }
    }

    public static SubmitOutcome Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.OK => SubmitOutcome.Sent,
        HttpStatusCode.Unauthorized => SubmitOutcome.AuthFailed,
        HttpStatusCode.TooManyRequests => SubmitOutcome.RetryLater,
        >= HttpStatusCode.InternalServerError => SubmitOutcome.RetryLater,
        _ => SubmitOutcome.Rejected,
    };

    /// <summary>The submit-listens body. "playing_now" listens have no timestamp.</summary>
    public static JsonObject BuildPayload(string listenType, IReadOnlyList<Scrobble> plays)
    {
        var payload = new JsonArray();
        foreach (var play in plays)
        {
            var info = new JsonObject
            {
                ["media_player"] = "Melora",
                ["submission_client"] = "Melora",
                ["submission_client_version"] = Version,
            };
            if (play.Duration > TimeSpan.Zero) info["duration_ms"] = (long)play.Duration.TotalMilliseconds;
            if (play.TrackNo > 0) info["tracknumber"] = play.TrackNo;

            var metadata = new JsonObject
            {
                ["artist_name"] = play.Artist,
                ["track_name"] = play.Title,
            };
            if (play.Album is { } album) metadata["release_name"] = album;
            metadata["additional_info"] = info;

            var listen = new JsonObject();
            if (listenType != "playing_now") listen["listened_at"] = play.StartedAt.ToUnixTimeSeconds();
            listen["track_metadata"] = metadata;
            payload.Add(listen);
        }

        return new JsonObject { ["listen_type"] = listenType, ["payload"] = payload };
    }

    async Task<HttpStatusCode> PostAsync(JsonObject body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Api + "submit-listens")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", Token);
        using var response = await _http.SendAsync(request);
        return response.StatusCode;
    }
}
