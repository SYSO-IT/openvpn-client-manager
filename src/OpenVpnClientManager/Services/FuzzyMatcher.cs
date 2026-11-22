namespace OpenVpnClientManager.Services;

/// <summary>
/// Small fzf-style fuzzy matcher. Every query character must appear in order in the text;
/// consecutive matches, word starts and exact substrings score higher, gaps score lower.
/// Multi-word queries must match every word (anywhere in the text).
/// </summary>
public static class FuzzyMatcher
{
    public static bool TryMatch(string query, string text, out int score, out List<int> positions)
    {
        score = 0;
        positions = [];
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var word in words)
        {
            if (!MatchWord(word, text, out var wordScore, out var wordPositions))
            {
                score = 0;
                positions.Clear();
                return false;
            }
            score += wordScore;
            positions.AddRange(wordPositions);
        }

        // Prefer shorter texts slightly so "Acme" ranks above "Acme Holding International".
        score -= Math.Min(text.Length / 10, 10);
        positions = positions.Distinct().OrderBy(p => p).ToList();
        return true;
    }

    private static bool MatchWord(string word, string text, out int score, out List<int> positions)
    {
        positions = new List<int>(word.Length);
        score = 0;

        // Exact substring: strong signal.
        int idx = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            score = 40 + word.Length * 4 + (IsWordStart(text, idx) ? 12 : 0) - Math.Min(idx, 10);
            for (int i = 0; i < word.Length; i++)
            {
                positions.Add(idx + i);
            }
            return true;
        }

        int t = 0;
        int lastPos = -2;
        for (int q = 0; q < word.Length; q++)
        {
            char qc = char.ToLowerInvariant(word[q]);
            int found = -1;
            for (; t < text.Length; t++)
            {
                if (char.ToLowerInvariant(text[t]) == qc)
                {
                    found = t;
                    break;
                }
            }
            if (found < 0)
            {
                score = 0;
                positions.Clear();
                return false;
            }

            score += 2;
            if (found == lastPos + 1)
            {
                score += 6; // consecutive
            }
            else if (lastPos >= 0)
            {
                score -= Math.Min(found - lastPos - 1, 6); // gap penalty
            }
            if (IsWordStart(text, found))
            {
                score += 5;
            }
            positions.Add(found);
            lastPos = found;
            t = found + 1;
        }
        return true;
    }

    private static bool IsWordStart(string text, int index)
    {
        if (index == 0)
        {
            return true;
        }
        char prev = text[index - 1];
        char cur = text[index];
        return char.IsWhiteSpace(prev) || prev is '-' or '_' or '.' or '/' or '\\' or '(' or '[' or ':'
               || (char.IsLower(prev) && char.IsUpper(cur));
    }
}
