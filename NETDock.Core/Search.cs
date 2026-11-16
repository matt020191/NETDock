namespace NETDock.Core;

public enum SearchKind { Dock, App, Recent }

/// <summary>One thing the search overlay can open. <paramref name="Target"/> is a path or shell parsing name.</summary>
public sealed record SearchEntry(string Name, string Target, SearchKind Kind, string? Detail = null);

/// <summary>Ranks entries against what's typed: whole name, then start, then word starts (incl. initials), then anywhere, then in order.</summary>
public static class SearchRanker
{
    /// <summary>Higher is better; -1 = no match.</summary>
    public static int Score(string query, string name)
    {
        query = query.Trim();
        if (query.Length == 0) return 0;
        if (name.Length == 0) return -1;
        var cmp = StringComparison.OrdinalIgnoreCase;
        if (name.Equals(query, cmp)) return 1000;
        if (name.StartsWith(query, cmp)) return 800 - Math.Min(100, name.Length - query.Length);
        var words = Words(name);
        if (words.Any(w => w.StartsWith(query, cmp))) return 650 - Math.Min(100, name.Length - query.Length);
        // Initials: "vsc" → Visual Studio Code.
        if (query.Length >= 2 && string.Concat(words.Select(w => char.ToLowerInvariant(w[0]))).StartsWith(query.ToLowerInvariant(), StringComparison.Ordinal))
            return 600;
        // Every typed word starts a word of the name: "stu vis" → Visual Studio.
        var parts = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && parts.All(p => words.Any(w => w.StartsWith(p, cmp)))) return 550;
        var at = name.IndexOf(query, cmp);
        if (at >= 0) return 400 - Math.Min(100, at);
        // Letters in order, fewer gaps first.
        var gaps = Subsequence(query.Replace(" ", ""), name);
        return gaps < 0 ? -1 : Math.Max(1, 200 - gaps * 5);
    }

    /// <summary>The best matches, dock items and apps before recent files when equally good, at most <paramref name="max"/>.</summary>
    public static List<SearchEntry> Rank(string query, IEnumerable<SearchEntry> entries, int max = 8)
    {
        var bonus = (SearchEntry e) => e.Kind switch { SearchKind.Dock => 30, SearchKind.App => 20, _ => 0 };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return entries
            .Select(e => (e, s: Score(query, e.Name)))
            .Where(x => x.s >= 0)
            .OrderByDescending(x => x.s + bonus(x.e))
            .ThenBy(x => x.e.Name.Length)
            .ThenBy(x => x.e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.e)
            .Where(e => seen.Add(e.Name)) // the same app from two sources once (the dock entry ranks first)
            .Take(max)
            .ToList();
    }

    private static List<string> Words(string name)
    {
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= name.Length; i++)
        {
            var sep = i == name.Length || !char.IsLetterOrDigit(name[i]);
            // Also split camel case: "OneNote" → One, Note.
            var camel = !sep && i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1]);
            if ((sep || camel) && start >= 0) { words.Add(name[start..i]); start = -1; }
            if (!sep && start < 0) start = i;
        }
        return words;
    }

    private static int Subsequence(string q, string name)
    {
        int gaps = 0, j = 0, last = -1;
        foreach (var c in q)
        {
            while (j < name.Length && char.ToLowerInvariant(name[j]) != char.ToLowerInvariant(c)) j++;
            if (j == name.Length) return -1;
            if (last >= 0) gaps += j - last - 1;
            last = j++;
        }
        return gaps;
    }
}
