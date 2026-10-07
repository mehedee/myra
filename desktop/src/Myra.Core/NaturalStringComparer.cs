using System.Globalization;

namespace Myra.Core;

/// Finder-style ordering: case-insensitive, with digit runs compared by value ("Episode 2" before "Episode 10").
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    private static readonly CompareInfo Culture = CultureInfo.InvariantCulture.CompareInfo;
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                var digits = a.SequenceCompareTo(b);
                if (digits != 0) return Math.Sign(digits);
                continue;
            }

            int ei = i, ej = j;
            while (ei < x.Length && !char.IsAsciiDigit(x[ei])) ei++;
            while (ej < y.Length && !char.IsAsciiDigit(y[ej])) ej++;
            var text = Culture.Compare(x, i, ei - i, y, j, ej - j, Options);
            if (text != 0) return Math.Sign(text);
            i = ei;
            j = ej;
        }
        var remainder = (x.Length - i).CompareTo(y.Length - j);
        return remainder != 0 ? remainder : Math.Sign(string.CompareOrdinal(x, y));
    }
}
