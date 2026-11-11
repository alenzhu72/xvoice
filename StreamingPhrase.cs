namespace VoiceChange;

// Paraformer sends a cumulative hypothesis for the current sentence.
internal sealed class StreamingPhrase
{
    private const int MinimumStableLength = 9;
    private const string Breaks = "，。！？；、,.!?;";
    private string previous = "";
    private string spoken = "";

    public string? Update(string text, bool final)
    {
        text = text.Trim();
        if (final)
        {
            var tail = text[FindFinalOffset(text)..].TrimStart(Breaks.ToCharArray()).Trim();
            previous = spoken = "";
            return tail.Length == 0 ? null : tail;
        }

        var stableEnd = 0;
        while (stableEnd < previous.Length && stableEnd < text.Length && previous[stableEnd] == text[stableEnd])
            stableEnd++;
        previous = text;

        if (!text.StartsWith(spoken, StringComparison.Ordinal) || stableEnd <= spoken.Length)
            return null;

        var end = stableEnd;
        while (end > spoken.Length && !Breaks.Contains(text[end - 1]))
            end--;
        if (end - spoken.Length < 4)
        {
            if (stableEnd - spoken.Length < MinimumStableLength)
                return null;
            // Keep the last two provisional characters for the next update.
            end = stableEnd - 2;
            while (end > spoken.Length && (char.IsLowSurrogate(text[end]) ||
                (char.IsAsciiLetterOrDigit(text[end]) && char.IsAsciiLetterOrDigit(text[end - 1]))))
                end--;
            if (end - spoken.Length < 4)
                return null;
        }

        var fragment = text[spoken.Length..end].TrimStart(Breaks.ToCharArray()).Trim();
        spoken = text[..end];
        return fragment.Length == 0 ? null : fragment;
    }

    private int FindFinalOffset(string text)
    {
        if (text.StartsWith(spoken, StringComparison.Ordinal))
            return spoken.Length;

        // The final result can add punctuation or revise a word already played.
        // Match the end of the played text when possible, without playing the prefix again.
        var before = WithoutPunctuation(spoken);
        var after = WithoutPunctuation(text);
        var count = Math.Min(before.Length, after.Length);
        if (!after.StartsWith(before, StringComparison.Ordinal) && before.Length >= 4)
        {
            var anchor = before[^4..];
            var left = Math.Clamp(before.Length - 8, 0, after.Length);
            var right = Math.Min(before.Length + 4, after.Length);
            var window = after.AsSpan(left, right - left);
            var match = window.IndexOf(anchor, StringComparison.Ordinal);
            if (match >= 0 && match == window.LastIndexOf(anchor, StringComparison.Ordinal))
                count = left + match + anchor.Length;
        }

        var offset = 0;
        while (offset < text.Length && count > 0)
        {
            if (IsContent(text[offset]))
                count--;
            offset++;
        }
        while (offset < text.Length && (!IsContent(text[offset]) || char.IsLowSurrogate(text[offset])))
            offset++;
        return offset;
    }

    private static string WithoutPunctuation(string value) => new(value.Where(IsContent).ToArray());
    private static bool IsContent(char value) => !char.IsPunctuation(value) && !char.IsWhiteSpace(value);
}
