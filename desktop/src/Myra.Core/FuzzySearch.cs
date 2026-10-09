using System.Globalization;
using System.Text;

namespace Myra.Core;

/// Bounded, deterministic spelling tolerance after SQLite retrieves candidate filenames
/// (port of macOS FuzzySearch). Candidate retrieval uses FTS5 unicode61 over three-letter grams.
public static class FuzzySearch
{
    /// Folds case and diacritics, then keeps only letters, marks and digits separated by single spaces.
    public static string Normalize(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var rune in decomposed.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            if (Rune.IsLetterOrDigit(rune) || Rune.IsNumber(rune)
                || category is UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                if (pendingSpace && builder.Length > 0) builder.Append(' ');
                pendingSpace = false;
                builder.Append(Rune.ToLowerInvariant(rune).ToString());
            }
            else
            {
                pendingSpace = true;
            }
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static IEnumerable<string> Grams(string word)
    {
        var letters = word.EnumerateRunes().Select(r => r.ToString()).ToArray();
        for (var position = 0; position + 3 <= letters.Length; position++)
            yield return string.Concat(letters[position], letters[position + 1], letters[position + 2]);
    }

    /// FTS5 MATCH expression of at most 64 quoted trigrams joined by OR; null when no token has three letters.
    public static string? MatchExpression(string query)
    {
        var fragments = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var gram in Grams(token))
            {
                if (seen.Add(gram)) fragments.Add("\"" + gram + "\"");
                if (fragments.Count == 64) break;
            }
            if (fragments.Count == 64) break;
        }
        return fragments.Count == 0 ? null : string.Join(" OR ", fragments);
    }

    /// Sorted, distinct trigrams of every word, space-separated (the FTS document body).
    public static string IndexGrams(string text)
    {
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var word in Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            foreach (var gram in Grams(word)) seen.Add(gram);
        return string.Join(' ', seen);
    }

    /// Null rejects a candidate; exact phrases precede prefix and approximate token matches. Lower is better.
    public static int? Score(string query, string name)
    {
        var needle = Normalize(query);
        var haystack = Normalize(name);
        if (needle.Length == 0 || needle.Length > 256) return null;
        if (haystack == needle) return 0;
        if (haystack.Contains(needle, StringComparison.Ordinal)) return 10;
        var available = haystack.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var total = 20;
        foreach (var word in needle.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int? best = null;
            foreach (var candidate in available)
            {
                int? cost;
                if (candidate == word)
                {
                    cost = 0;
                }
                else if (candidate.StartsWith(word, StringComparison.Ordinal))
                {
                    cost = 1;
                }
                else if (word.EnumerateRunes().All(Rune.IsNumber) || word.EnumerateRunes().Count() < 4)
                {
                    cost = null;
                }
                else
                {
                    var threshold = word.EnumerateRunes().Count() >= 8 ? 2 : 1;
                    var distance = EditDistance(word, candidate, threshold);
                    cost = distance <= threshold ? 10 + distance : null;
                }
                if (cost is { } value) best = Math.Min(best ?? value, value);
            }
            if (best is not { } found) return null;
            total += found;
        }
        return total;
    }

    /// Levenshtein distance, or maximum + 1 once it must exceed maximum.
    public static int EditDistance(string left, string right, int maximum)
    {
        var a = left.EnumerateRunes().ToArray();
        var b = right.EnumerateRunes().ToArray();
        if (Math.Abs(a.Length - b.Length) > maximum) return maximum + 1;
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 0; i < a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i + 1;
            var rowMinimum = current[0];
            for (var j = 0; j < b.Length; j++)
            {
                current[j + 1] = Math.Min(Math.Min(previous[j + 1] + 1, current[j] + 1), previous[j] + (a[i] == b[j] ? 0 : 1));
                rowMinimum = Math.Min(rowMinimum, current[j + 1]);
            }
            if (rowMinimum > maximum) return maximum + 1;
            previous = current;
        }
        return previous[^1];
    }
}
