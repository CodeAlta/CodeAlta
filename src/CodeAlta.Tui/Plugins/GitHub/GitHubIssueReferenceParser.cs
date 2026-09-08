namespace CodeAlta.Plugin.GitHub;

internal static class GitHubIssueReferenceParser
{
    public static bool TryGetActiveIssueReference(string text, int caretIndex, out GitHubIssueReferenceSpan reference)
    {
        reference = default;
        if (string.IsNullOrEmpty(text) || caretIndex <= 0 || caretIndex > text.Length)
        {
            return false;
        }

        var start = caretIndex - 1;
        while (start >= 0 && text[start] != '#' && text[start] is not '\r' and not '\n')
        {
            start--;
        }

        if (start < 0 || text[start] != '#')
        {
            return false;
        }

        if (start > 0 && !char.IsWhiteSpace(text[start - 1]))
        {
            return false;
        }

        for (var index = start + 1; index < caretIndex; index++)
        {
            var ch = text[index];
            if (ch is '\r' or '\n' or '[' or ']' or '(' or ')')
            {
                return false;
            }
        }

        reference = new GitHubIssueReferenceSpan(start, caretIndex - start, text.Substring(start + 1, caretIndex - start - 1));
        return true;
    }
}

internal readonly record struct GitHubIssueReferenceSpan(int StartIndex, int Length, string QueryText);
