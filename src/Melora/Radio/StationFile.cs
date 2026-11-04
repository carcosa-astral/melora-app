using System.Text;

namespace Melora.Radio;

/// <summary>
/// Stations saved to, and read from, a file: an M3U list with each station's name, which other
/// players can open too. Reading also takes plain lists of addresses, named after their host.
/// </summary>
public static class StationFile
{
    const string Header = "#EXTM3U";
    const string Info = "#EXTINF:";

    public static string Write(IEnumerable<(string Name, string Url)> stations)
    {
        var text = new StringBuilder(Header).Append("\r\n");
        foreach (var (name, url) in stations)
            text.Append(Info).Append("-1,").Append(name).Append("\r\n").Append(url).Append("\r\n");
        return text.ToString();
    }

    /// <summary>The stations in a file's text; lines that aren't web addresses are skipped.</summary>
    public static List<(string Name, string Url)> Read(string text)
    {
        var stations = new List<(string, string)>();
        string? name = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(Info, StringComparison.OrdinalIgnoreCase))
            {
                // "#EXTINF:-1,Name" (the name can contain commas; the duration can't)
                var comma = line.IndexOf(',');
                name = comma >= 0 && line[(comma + 1)..].Trim() is { Length: > 0 } n ? n : null;
            }
            else if (line.Length > 0 && !line.StartsWith('#') && StreamMetadata.NormalizeUrl(line) is { } url)
            {
                stations.Add((name ?? new Uri(url).Host, url));
                name = null;
            }
        }
        return stations;
    }
}
