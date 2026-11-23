using System.Buffers;

namespace RuleOpen.Core.Rules;

/// <summary>
/// Normalizes and parses file extension tokens such as ".pdf", "PDF", "*.md", or ".md, .markdown, .txt".
/// </summary>
public static class ExtensionNormalizer
{
    private static readonly char[] TokenSeparators = [',', ';', ' ', '\t', '\r', '\n'];
    private static readonly SearchValues<char> DisallowedExtensionChars =
        SearchValues.Create(['/', '\\', ':', '*', '?', '"', '<', '>', '|']);

    public static bool TryNormalize(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string token = raw.Trim();
        if (token.StartsWith("*.", StringComparison.Ordinal))
        {
            token = token[1..];
        }
        else if (token.StartsWith('*'))
        {
            token = token[1..];
        }

        token = token.TrimStart('.');
        if (token.Length == 0)
        {
            return false;
        }

        if (token.AsSpan().ContainsAny(DisallowedExtensionChars) ||
            token.Any(char.IsWhiteSpace) ||
            token.Any(char.IsControl) ||
            token.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        normalized = "." + token.ToLowerInvariant();
        return true;
    }

    public static (IReadOnlyList<string> Extensions, IReadOnlyList<string> InvalidTokens) ParseList(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return ([], []);
        }

        List<string> valid = [];
        List<string> invalid = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string rawToken in input.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryNormalize(rawToken, out string normalized))
            {
                if (seen.Add(normalized))
                {
                    valid.Add(normalized);
                }
            }
            else
            {
                invalid.Add(rawToken);
            }
        }

        return (valid, invalid);
    }

    public static IReadOnlyList<string> NormalizeMany(IEnumerable<string>? extensions)
    {
        if (extensions is null)
        {
            return [];
        }

        List<string> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string item in extensions)
        {
            if (TryNormalize(item, out string normalized) && seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }
}
