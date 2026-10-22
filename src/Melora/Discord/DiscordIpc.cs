using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace Melora.Discord;

public enum DiscordConnect { Ready, NotRunning, Refused }

/// <summary>
/// Talks to the Discord desktop app over its local named pipe (discord-ipc-0 to 9). Each message is
/// a little-endian opcode and length followed by JSON. No login or internet involved: Discord shows
/// whatever the app on this PC tells it.
/// </summary>
public sealed class DiscordIpc : IDisposable
{
    const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;
    static readonly TimeSpan PipeTimeout = TimeSpan.FromMilliseconds(250);

    readonly SemaphoreSlim _writeLock = new(1, 1);
    NamedPipeClientStream? _pipe;

    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>Why Discord refused the connection (e.g. an unknown application ID).</summary>
    public string? RefusedReason { get; private set; }

    public async Task<DiscordConnect> ConnectAsync(string applicationId, CancellationToken cancel)
    {
        for (var i = 0; i < 10 && _pipe is null; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(PipeTimeout, cancel);
                _pipe = pipe;
            }
            catch (Exception e) when (e is TimeoutException or IOException)
            {
                await pipe.DisposeAsync();
            }
        }
        if (_pipe is null) return DiscordConnect.NotRunning;

        try
        {
            await WriteAsync(OpHandshake, new JsonObject { ["v"] = 1, ["client_id"] = applicationId }, cancel);
            var (op, reply) = await ReadAsync(cancel);
            if (op == OpFrame && reply?["evt"]?.GetValue<string>() == "READY")
            {
                _ = ReadLoopAsync(_pipe);
                return DiscordConnect.Ready;
            }
            RefusedReason = reply?["message"]?.GetValue<string>() ?? "Discord closed the connection.";
            Disconnect();
            return DiscordConnect.Refused;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException)
        {
            Disconnect();
            return DiscordConnect.NotRunning;
        }
    }

    /// <summary>Sets what Melora shows on the user's profile; null clears it.</summary>
    public Task SetActivityAsync(JsonObject? activity, CancellationToken cancel) =>
        WriteAsync(OpFrame, new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = new JsonObject { ["pid"] = Environment.ProcessId, ["activity"] = activity },
            ["nonce"] = Guid.NewGuid().ToString(),
        }, cancel);

    /// <summary>Drains replies (answering pings) until the pipe closes, so Discord never blocks on a full pipe.</summary>
    async Task ReadLoopAsync(NamedPipeClientStream pipe)
    {
        try
        {
            while (_pipe == pipe)
            {
                var (op, body) = await ReadAsync(CancellationToken.None);
                if (op == OpPing) await WriteAsync(OpPong, body ?? new JsonObject(), CancellationToken.None);
                else if (op == OpClose) break;
            }
        }
        catch
        {
            // The pipe broke: Discord quit or restarted.
        }
        if (_pipe == pipe) Disconnect();
    }

    async Task WriteAsync(int op, JsonNode body, CancellationToken cancel)
    {
        var pipe = _pipe ?? throw new IOException("Not connected to Discord.");
        var json = Encoding.UTF8.GetBytes(body.ToJsonString());
        var frame = new byte[8 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), json.Length);
        json.CopyTo(frame, 8);

        await _writeLock.WaitAsync(cancel);
        try
        {
            await pipe.WriteAsync(frame, cancel);
            await pipe.FlushAsync(cancel);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    async Task<(int Op, JsonNode? Body)> ReadAsync(CancellationToken cancel)
    {
        var pipe = _pipe ?? throw new IOException("Not connected to Discord.");
        var header = new byte[8];
        await pipe.ReadExactlyAsync(header, cancel);
        var op = BinaryPrimitives.ReadInt32LittleEndian(header);
        var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4))];
        await pipe.ReadExactlyAsync(body, cancel);
        return (op, JsonNode.Parse(body));
    }

    public void Disconnect()
    {
        var pipe = _pipe;
        _pipe = null;
        pipe?.Dispose(); // Discord clears the activity when the connection goes
    }

    public void Dispose() => Disconnect();
}
