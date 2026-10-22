using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json.Nodes;
using Melora.Library;

namespace Melora.Discord;

public enum PresenceSource { Music, Mixes, Radio }

/// <summary>What Discord should show. Start and End drive its progress bar; both are null while paused.</summary>
public sealed record PresenceInfo(
    PresenceSource Source,
    string Details,
    string State,
    string? LargeText,
    CoverQuery? Cover,
    DateTimeOffset? Start,
    DateTimeOffset? End);

/// <summary>
/// Shows what's playing as a "Listening to" status on the user's Discord profile. Updates are sent
/// from a background loop: coalesced (Discord allows about one every few seconds), with the cover
/// looked up first, and reconnecting now and then if Discord wasn't running.
/// </summary>
public sealed class DiscordPresence : ObservableObject, IDisposable
{
    static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(750);
    static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2);
    static readonly TimeSpan RetryConnect = TimeSpan.FromSeconds(15);
    const int MaxText = 128;
    const string Idle = "Connects to Discord once something plays.";

    readonly LibraryDb _db;
    readonly CoverLookup _covers;
    readonly DiscordIpc _ipc = new();
    readonly SemaphoreSlim _wake = new(0);
    readonly CancellationTokenSource _stop = new();
    readonly Stopwatch _sinceSend = Stopwatch.StartNew();
    PresenceInfo? _wanted;
    PresenceInfo? _shown;
    string? _connectedId;
    DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    public DiscordPresence(LibraryDb db, CoverLookup covers)
    {
        _db = db;
        _covers = covers;
        _status = Enabled ? Idle : "Off";
    }

    public void Start() => _ = Task.Run(RunAsync);

    // ---- Settings ----

    public bool Enabled
    {
        get => _db.GetSetting("discord.enabled") == "1";
        set
        {
            _db.SetSetting("discord.enabled", value ? "1" : "0");
            OnPropertyChanged();
            Status = value ? Idle : "Off";
            Reconnect();
        }
    }

    /// <summary>The Application ID from Discord's developer portal; its name is what shows after "Listening to".</summary>
    public string ApplicationId
    {
        get => _db.GetSetting("discord.appId") ?? "";
        set
        {
            var id = new string(value.Where(char.IsAsciiDigit).ToArray());
            if (id == ApplicationId) return;
            _db.SetSetting("discord.appId", id);
            OnPropertyChanged();
            Reconnect();
        }
    }

    public bool ShowMixes
    {
        get => _db.GetSetting("discord.mixes") != "0";
        set
        {
            _db.SetSetting("discord.mixes", value ? "1" : "0");
            OnPropertyChanged();
            Wake();
        }
    }

    public bool ShowRadio
    {
        get => _db.GetSetting("discord.radio") != "0";
        set
        {
            _db.SetSetting("discord.radio", value ? "1" : "0");
            OnPropertyChanged();
            Wake();
        }
    }

    string _status;
    /// <summary>A line for Settings: connected, waiting for Discord, or what went wrong. Set from the background loop.</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value) && value.StartsWith("Discord didn't", StringComparison.Ordinal)) Log.Write(value);
        }
    }

    // ---- Updates ----

    /// <summary>What's playing now (null for nothing). Cheap; call on every change.</summary>
    public void Show(PresenceInfo? info)
    {
        _wanted = info;
        Wake();
    }

    void Reconnect()
    {
        _lastAttempt = DateTimeOffset.MinValue;
        Wake();
    }

    void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    async Task RunAsync()
    {
        var cancel = _stop.Token;
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                // Wake on a change, or now and then to retry a Discord that wasn't running.
                if (await _wake.WaitAsync(RetryConnect, cancel)) await Task.Delay(Settle, cancel);
                await SyncAsync(cancel);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                // Never let Discord trouble disturb playback; drop the connection and try again later.
                Log.Write($"Discord: {e.Message}");
                _ipc.Disconnect();
                _shown = null;
            }
        }
    }

    async Task SyncAsync(CancellationToken cancel)
    {
        var id = ApplicationId;
        var wanted = Enabled && id.Length > 0 ? Filter(_wanted) : null;

        // Off, or a different application ID: drop the connection (which also clears the status).
        if (_ipc.IsConnected && (!Enabled || id != _connectedId))
        {
            _ipc.Disconnect();
            _shown = null;
        }
        if (!Enabled) return;
        if (id.Length == 0)
        {
            Status = "Paste your application ID to turn this on.";
            return;
        }

        if (!_ipc.IsConnected)
        {
            _shown = null;
            if (wanted is null || DateTimeOffset.UtcNow - _lastAttempt < RetryConnect) return;
            _lastAttempt = DateTimeOffset.UtcNow;
            switch (await _ipc.ConnectAsync(id, cancel))
            {
                case DiscordConnect.NotRunning:
                    Status = "Waiting for Discord to start.";
                    return;
                case DiscordConnect.Refused:
                    Status = $"Discord didn't accept that application ID ({_ipc.RefusedReason}).";
                    return;
            }
            _connectedId = id;
            Status = "Connected to Discord.";
        }

        if (wanted == _shown) return;
        var wait = MinInterval - _sinceSend.Elapsed;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, cancel);

        // Something newer may have come in meanwhile; the next pass picks it up.
        var cover = wanted?.Cover is { } query ? await _covers.FindAsync(query) : null;
        await _ipc.SetActivityAsync(wanted is null ? null : BuildActivity(wanted, cover), cancel);
        _sinceSend.Restart();
        _shown = wanted;
        if (Filter(_wanted) != wanted) Wake();
    }

    PresenceInfo? Filter(PresenceInfo? info) => info?.Source switch
    {
        PresenceSource.Mixes when !ShowMixes => null,
        PresenceSource.Radio when !ShowRadio => null,
        _ => info,
    };

    /// <summary>The activity as Discord wants it: type 2 is "Listening to", timestamps in milliseconds.</summary>
    public static JsonObject BuildActivity(PresenceInfo info, string? coverUrl)
    {
        var activity = new JsonObject
        {
            ["type"] = 2,
            ["details"] = Fit(info.Details),
            ["state"] = Fit(info.State),
        };

        if (info.Start is { } start)
        {
            var timestamps = new JsonObject { ["start"] = start.ToUnixTimeMilliseconds() };
            if (info.End is { } end) timestamps["end"] = end.ToUnixTimeMilliseconds();
            activity["timestamps"] = timestamps;
        }

        // Without an image, Discord shows the application's own icon.
        if (coverUrl is not null)
        {
            var assets = new JsonObject { ["large_image"] = coverUrl };
            if (info.LargeText is { } text) assets["large_text"] = Fit(text);
            activity["assets"] = assets;
        }
        return activity;
    }

    /// <summary>Discord wants 2 to 128 characters in each text field.</summary>
    public static string Fit(string text)
    {
        if (text.Length > MaxText) return text[..(MaxText - 1)] + "…";
        return text.Length < 2 ? text.PadRight(2, '⠀') : text;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _ipc.Dispose();
    }
}
