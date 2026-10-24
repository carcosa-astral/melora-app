using System.Globalization;

namespace Melora;

/// <summary>Type to jump: which row typing letters in a list jumps to. Not a search; nothing is filtered.</summary>
public static class ListJump
{
    const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    /// <summary>
    /// The first item (in list order) whose name starts with what was typed, ignoring case, accents and
    /// a leading "The". Typing one letter again and again ("bbb") steps through the names starting with
    /// it, the way Explorer does. Null when nothing matches.
    /// </summary>
    public static T? Find<T>(IEnumerable<T> items, Func<T, string> name, string typed) where T : class
    {
        if (typed.Trim().Length == 0) return null;
        var repeated = typed.Length > 1 && typed.All(c => char.ToLowerInvariant(c) == char.ToLowerInvariant(typed[0]));
        var prefix = repeated ? typed[..1] : typed;

        // The first item of each matching name: an artist's albums or a DJ's mixes count once.
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var starts = new List<T>();
        foreach (var item in items)
        {
            var n = name(item);
            if (seen.Add(n) && StartsWith(n, prefix)) starts.Add(item);
        }
        if (starts.Count == 0) return null;
        return repeated ? starts[(typed.Length - 1) % starts.Count] : starts[0];
    }

    static bool StartsWith(string name, string prefix)
    {
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        if (compare.IsPrefix(name, prefix, Loose)) return true;
        return name.StartsWith("The ", StringComparison.OrdinalIgnoreCase) && compare.IsPrefix(name[4..], prefix, Loose);
    }
}
