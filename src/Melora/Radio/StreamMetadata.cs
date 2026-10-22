namespace Melora.Radio;

/// <summary>Parsing for what internet radio sends: ICY headers, ICY titles and Ogg comments.</summary>
public static class StreamMetadata
{
    /// <summary>Pulls the title out of an ICY block like <c>StreamTitle='Artist - Title';StreamUrl='';</c>.</summary>
    public static string? ParseIcyTitle(string? meta)
    {
        const string key = "StreamTitle='";
        if (string.IsNullOrEmpty(meta)) return null;

        var start = meta.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += key.Length;

        // Titles can contain apostrophes ("Guns N' Roses"), so end at the "';" that closes the field.
        var end = meta.IndexOf("';", start, StringComparison.Ordinal);
        if (end < 0) end = meta.LastIndexOf('\'');
        if (end < start) return null;

        return Clean(meta[start..end]);
    }

    /// <summary>Splits a stream title like "Artist - Title" at the first " - ". Either part may be null.</summary>
    public static (string? Artist, string? Title) SplitArtistTitle(string? streamTitle)
    {
        if (Clean(streamTitle) is not { } text) return (null, null);
        var dash = text.IndexOf(" - ", StringComparison.Ordinal);
        return dash < 0 ? (null, text) : (Clean(text[..dash]), Clean(text[(dash + 3)..]));
    }

    /// <summary>Builds "Artist - Title" from Ogg/Vorbis comments such as <c>ARTIST=...</c> and <c>TITLE=...</c>.</summary>
    public static string? FromVorbisComments(IEnumerable<string> comments)
    {
        string? artist = null, title = null;
        foreach (var c in comments)
        {
            artist ??= Value(c, "ARTIST=");
            title ??= Value(c, "TITLE=");
        }

        return (Clean(artist), Clean(title)) switch
        {
            ({ } a, { } t) => $"{a} - {t}",
            (null, { } t) => t,
            ({ } a, null) => a,
            _ => null,
        };

        static string? Value(string comment, string key) =>
            comment.StartsWith(key, StringComparison.OrdinalIgnoreCase) ? comment[key.Length..] : null;
    }

    /// <summary>Finds a header such as <c>icy-name: Groove Salad</c> in a list of raw header lines.</summary>
    public static string? HeaderValue(IEnumerable<string> headers, string name)
    {
        foreach (var line in headers)
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line.AsSpan(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return Clean(line[(colon + 1)..]);
        }
        return null;
    }

    /// <summary>Tidies a pasted address: trims it and adds http:// when there's no scheme. Null if it isn't a web address.</summary>
    public static string? NormalizeUrl(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;

        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && !string.IsNullOrEmpty(uri.Host)
            ? text
            : null;
    }

    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
