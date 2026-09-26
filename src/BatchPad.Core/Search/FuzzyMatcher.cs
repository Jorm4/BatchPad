namespace BatchPad.Core.Search;

/// <summary>Subsequence matching that favours word starts and consecutive letters, as command palettes do.</summary>
public static class FuzzyMatcher
{
    private const int Match = 1;
    private const int WordStart = 10;
    private const int Consecutive = 8;
    private const int MaxGapPenalty = 5;
    private const int None = int.MinValue;

    /// <returns>Higher is better; null when <paramref name="query"/> is not a subsequence of <paramref name="candidate"/>.</returns>
    public static int? Score(string query, string candidate)
    {
        var q = query.Where(c => !char.IsWhiteSpace(c)).Select(char.ToLowerInvariant).ToArray();
        if (q.Length == 0)
            return 0;
        var n = candidate.Length;
        if (q.Length > n)
            return null;

        var previous = new int[n];
        var current = new int[n];
        for (var i = 0; i < q.Length; i++)
        {
            var farBest = None;
            for (var j = 0; j < n; j++)
            {
                var far = j - MaxGapPenalty - 1;
                if (i > 0 && far - 1 >= 0)
                    farBest = Math.Max(farBest, previous[far - 1]);
                current[j] = None;
                if (char.ToLowerInvariant(candidate[j]) != q[i])
                    continue;
                var own = Match + (IsWordStart(candidate, j) ? WordStart : 0);
                if (i == 0)
                {
                    current[j] = own - Math.Min(j, 3);
                    continue;
                }
                var best = farBest == None ? None : farBest - MaxGapPenalty;
                if (j > 0 && previous[j - 1] != None)
                    best = Math.Max(best, previous[j - 1] + Consecutive);
                for (var k = Math.Max(0, far); k < j - 1; k++)
                    if (previous[k] != None)
                        best = Math.Max(best, previous[k] - (j - k - 1));
                if (best != None)
                    current[j] = best + own;
            }
            (previous, current) = (current, previous);
        }
        var result = previous.Max();
        return result == None ? null : result;
    }

    /// <summary>The matching items, best first; ties keep the shorter text, then the original order.</summary>
    public static IEnumerable<T> Rank<T>(string query, IEnumerable<T> items, Func<T, string> text) =>
        items.Select((item, index) => (Item: item, Index: index, Text: text(item)))
            .Select(r => (r.Item, r.Index, r.Text, Score: Score(query, r.Text)))
            .Where(r => r.Score is not null)
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Text.Length)
            .ThenBy(r => r.Index)
            .Select(r => r.Item);

    private static bool IsWordStart(string text, int index)
    {
        if (index == 0)
            return true;
        char before = text[index - 1], at = text[index];
        return !char.IsLetterOrDigit(before)
            || (char.IsLower(before) && char.IsUpper(at))
            || (char.IsLetter(before) && char.IsDigit(at));
    }
}
