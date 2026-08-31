namespace InterviewTranscriberV5;

public sealed class TranscriptAccumulator
{
    private readonly object _sync = new();
    private string _committed = string.Empty;
    private string _current = string.Empty;
    private bool _newLineBeforeNext;

    public string UpdateProvisional(string hypothesis)
    {
        lock (_sync)
        {
            _current = MergeRollingText(_current, hypothesis);
            return Render();
        }
    }

    public string CommitFinal(string hypothesis)
    {
        lock (_sync)
        {
            hypothesis = CleanWhitespace(hypothesis);
            if (!string.IsNullOrWhiteSpace(hypothesis)) _current = hypothesis;
            if (!string.IsNullOrWhiteSpace(_current))
                _committed = Join(_committed.TrimEnd(), _current, _newLineBeforeNext);

            _current = string.Empty;
            _newLineBeforeNext = true;
            return Render();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _committed = string.Empty;
            _current = string.Empty;
            _newLineBeforeNext = false;
        }
    }

    private string Render() => Join(_committed.TrimEnd(), _current, _newLineBeforeNext);

    private static string MergeRollingText(string existing, string incoming)
    {
        existing = CleanWhitespace(existing);
        incoming = CleanWhitespace(incoming);
        if (existing.Length == 0) return incoming;
        if (incoming.Length == 0) return existing;
        if (string.Equals(existing, incoming, StringComparison.OrdinalIgnoreCase)) return incoming;
        if (incoming.Contains(existing, StringComparison.OrdinalIgnoreCase)) return incoming;
        if (existing.Contains(incoming, StringComparison.OrdinalIgnoreCase)) return existing;

        string[] oldWords = existing.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] newWords = incoming.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int maxOverlap = Math.Min(Math.Min(oldWords.Length, newWords.Length), 20);

        for (int overlap = maxOverlap; overlap >= 1; overlap--)
        {
            bool matches = Enumerable.Range(0, overlap).All(i =>
                string.Equals(
                    NormalizeWord(oldWords[oldWords.Length - overlap + i]),
                    NormalizeWord(newWords[i]),
                    StringComparison.OrdinalIgnoreCase));
            if (matches) return Join(string.Join(' ', oldWords), string.Join(' ', newWords.Skip(overlap)));
        }

        int tailStart = Math.Max(0, oldWords.Length - 12);
        for (int prefixLength = Math.Min(newWords.Length, 8); prefixLength >= 2; prefixLength--)
        {
            for (int start = oldWords.Length - prefixLength; start >= tailStart; start--)
            {
                bool matches = Enumerable.Range(0, prefixLength).All(i =>
                    string.Equals(NormalizeWord(oldWords[start + i]), NormalizeWord(newWords[i]), StringComparison.OrdinalIgnoreCase));
                if (matches) return CleanWhitespace(string.Join(' ', oldWords.Take(start).Concat(newWords)));
            }
        }

        return newWords.Length >= 3 && oldWords.Length <= 5 ? incoming : Join(existing, incoming);
    }

    private static string Join(string left, string right, bool newLine = false)
    {
        left = left.TrimEnd();
        right = CleanWhitespace(right);
        if (left.Length == 0) return right;
        if (right.Length == 0) return left;
        if (newLine) return left + Environment.NewLine + right;
        return ".,;:!?)]}".Contains(right[0]) ? left + right : left + " " + right;
    }

    private static string CleanWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string NormalizeWord(string word) =>
        word.Trim('.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '{', '}');
}
