using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Text.Json.Nodes;
using Melora.Library;

namespace Melora;

/// <summary>
/// Tells you when a newer Melora is on GitHub. It only looks and links to the release page; it never
/// downloads or installs anything (Melora is a folder you replace yourself). Checks at most once a day.
/// </summary>
public sealed class UpdateCheck : ObservableObject
{
    const string LatestRelease = "https://api.github.com/repos/carcosa-astral/melora-app/releases/latest";
    public const string ReleasesPage = "https://github.com/carcosa-astral/melora-app/releases";
    static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    readonly LibraryDb _db;
    readonly HttpClient _http;

    public UpdateCheck(LibraryDb db)
    {
        _db = db;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Melora/{CurrentVersion}");
    }

    /// <summary>This copy's version, e.g. "1.0.0".</summary>
    public static string CurrentVersion { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public bool Enabled
    {
        get => _db.GetSetting("update.enabled") != "0";
        set
        {
            _db.SetSetting("update.enabled", value ? "1" : "0");
            OnPropertyChanged();
            OnPropertyChanged(nameof(Status));
            if (value) _ = CheckAsync();
        }
    }

    /// <summary>The newest version on GitHub, as of the last check; null if never checked.</summary>
    public string? Latest => _db.GetSetting("update.latest");

    /// <summary>The newest release's page.</summary>
    public string ReleaseUrl => _db.GetSetting("update.url") ?? ReleasesPage;

    /// <summary>A newer version than this one is out.</summary>
    public bool IsAvailable => Enabled && IsNewer(Latest, CurrentVersion);

    bool _checking;
    public bool IsChecking { get => _checking; private set { if (Set(ref _checking, value)) OnPropertyChanged(nameof(Status)); } }

    bool _failed;

    /// <summary>One line for Settings.</summary>
    public string Status
    {
        get
        {
            if (!Enabled) return "Not checking for new versions.";
            if (IsChecking) return "Checking…";
            if (IsAvailable) return $"Melora {Latest} is available.";
            if (_failed) return "Couldn't reach GitHub to check for a new version.";
            return Latest is null ? "" : "This is the newest version.";
        }
    }

    /// <summary>Checks if it's been a day since the last time (called at startup).</summary>
    public Task CheckIfDueAsync()
    {
        var last = long.TryParse(_db.GetSetting("update.checkedAt"), out var s) ? DateTimeOffset.FromUnixTimeSeconds(s) : DateTimeOffset.MinValue;
        return Enabled && DateTimeOffset.UtcNow - last >= Interval ? CheckAsync() : Task.CompletedTask;
    }

    /// <summary>Asks GitHub for the latest release now. Never throws.</summary>
    public async Task CheckAsync()
    {
        if (IsChecking || !Enabled) return;
        IsChecking = true;
        try
        {
            var json = JsonNode.Parse(await _http.GetStringAsync(LatestRelease));
            if (ParseTag(json?["tag_name"]?.GetValue<string>()) is { } latest)
            {
                _db.SetSetting("update.latest", latest.ToString(3));
                if (json?["html_url"]?.GetValue<string>() is { } url) _db.SetSetting("update.url", url);
            }
            _db.SetSetting("update.checkedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            _failed = false;
        }
        catch (Exception e)
        {
            // Offline is normal; the next start tries again.
            _failed = true;
            Log.Write($"Update check: {e.Message}");
        }
        finally
        {
            IsChecking = false;
            OnPropertyChanged(nameof(Latest));
            OnPropertyChanged(nameof(ReleaseUrl));
            OnPropertyChanged(nameof(IsAvailable));
            OnPropertyChanged(nameof(Status));
        }
    }

    /// <summary>"v1.2.0" or "1.2" → 1.2.0; null for anything that isn't a version.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (tag is null) return null;
        var text = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(text, out var version)) return null;
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    /// <summary>Whether <paramref name="latest"/> is a later version than <paramref name="current"/>.</summary>
    public static bool IsNewer(string? latest, string current) =>
        ParseTag(latest) is { } l && ParseTag(current) is { } c && l > c;
}
