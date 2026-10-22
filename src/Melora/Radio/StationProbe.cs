using ManagedBass;
using Melora.Audio;

namespace Melora.Radio;

public sealed record ProbeResult(bool Ok, string? Name, string? Genre, string? Format, string? Error);

/// <summary>
/// Connects to a pasted stream address without playing it, to check it works and
/// read the name and format the station advertises.
/// </summary>
public static class StationProbe
{
    public static Task<ProbeResult> ProbeAsync(string url) => Task.Run(() =>
    {
        var handle = Bass.CreateStream(url, 0, BassFlags.Decode | BassFlags.Float, null, IntPtr.Zero);
        if (handle == 0) return new ProbeResult(false, null, null, null, Describe(Bass.LastError));

        try
        {
            var headers = AudioEngine.ReadTags(handle, TagType.ICY).Concat(AudioEngine.ReadTags(handle, TagType.HTTP)).ToList();
            var codec = Bass.ChannelGetInfo(handle, out var info) ? AudioEngine.CodecName(info.ChannelType) : null;
            var kbps = StreamMetadata.HeaderValue(headers, "icy-br")?.Split(',')[0];
            var format = string.Join("  ·  ", new[] { codec, kbps is null ? null : $"{kbps} kbps" }.OfType<string>());

            return new ProbeResult(
                true,
                StreamMetadata.HeaderValue(headers, "icy-name"),
                StreamMetadata.HeaderValue(headers, "icy-genre"),
                format.Length > 0 ? format : null,
                null);
        }
        finally
        {
            Bass.StreamFree(handle);
        }
    });

    /// <summary>A friendly sentence for why a stream couldn't be opened.</summary>
    public static string Describe(Errors error) => error switch
    {
        Errors.Timeout => "The station didn't answer in time.",
        Errors.FileOpen => "Couldn't connect to that address.",
        Errors.FileFormat or Errors.Codec => "That address isn't an audio stream Melora can play.",
        Errors.NoInternet => "No internet connection.",
        Errors.SSL => "The secure connection failed.",
        Errors.Unstreamable => "That stream can't be played live.",
        _ => $"Couldn't play the stream ({error}).",
    };
}
