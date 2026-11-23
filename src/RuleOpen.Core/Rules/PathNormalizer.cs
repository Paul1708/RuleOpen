using System.Buffers;

namespace RuleOpen.Core.Rules;

/// <summary>
/// Normalizes Windows file and folder paths cleanly and prevents directory-prefix collisions.
/// </summary>
public static class PathNormalizer
{
    private static readonly SearchValues<char> InvalidCharacters =
        SearchValues.Create([.. Path.GetInvalidPathChars(), '*', '?', '"', '<', '>', '|']);

    public static bool TryNormalizeFolderPath(string? rawPath, out string normalizedFolder)
    {
        normalizedFolder = string.Empty;
        if (!TryGetCleanedFullPath(rawPath, isDirectory: true, out string fullPath))
        {
            return false;
        }

        normalizedFolder = StripTrailingDirectorySeparator(fullPath);
        return true;
    }

    public static bool TryNormalizeFilePath(
        string? rawPath,
        out string normalizedFilePath,
        out string normalizedParentFolder,
        out string? normalizedExtension)
    {
        normalizedFilePath = string.Empty;
        normalizedParentFolder = string.Empty;
        normalizedExtension = null;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return false;
        }

        string trimmed = rawPath.Trim().Trim('"');
        if (trimmed.EndsWith('\\') || trimmed.EndsWith('/'))
        {
            return false;
        }

        if (!TryGetCleanedFullPath(trimmed, isDirectory: false, out string fullPath))
        {
            return false;
        }

        string fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        string? parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return false;
        }

        normalizedFilePath = fullPath;
        normalizedParentFolder = StripTrailingDirectorySeparator(parent);

        string rawExt = Path.GetExtension(fileName);
        if (!string.IsNullOrEmpty(rawExt) && ExtensionNormalizer.TryNormalize(rawExt, out string ext))
        {
            normalizedExtension = ext;
        }

        return true;
    }

    public static int GetFolderDepth(string normalizedFolder)
    {
        if (string.IsNullOrWhiteSpace(normalizedFolder))
        {
            return 0;
        }

        if (normalizedFolder.StartsWith(@"\\", StringComparison.Ordinal))
        {
            string[] segments = normalizedFolder.TrimStart('\\')
                .Split('\\', StringSplitOptions.RemoveEmptyEntries);
            // UNC root (\\server\share) has 2 segments -> depth 1.
            return Math.Max(1, segments.Length - 1);
        }

        if (normalizedFolder.Length >= 3 && normalizedFolder[1] == ':' && normalizedFolder[2] == '\\')
        {
            string tail = normalizedFolder[3..];
            if (tail.Length == 0)
            {
                return 1;
            }

            int extraSegments = tail.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;
            return 1 + extraSegments;
        }

        return normalizedFolder.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    public static string EnsureTrailingSeparator(string normalizedFolder)
    {
        if (string.IsNullOrEmpty(normalizedFolder))
        {
            return string.Empty;
        }

        return normalizedFolder.EndsWith('\\')
            ? normalizedFolder
            : normalizedFolder + '\\';
    }

    public static bool IsFolderMatch(
        string normalizedRuleFolder,
        bool includeSubfolders,
        string normalizedFileParentFolder,
        out bool isExactMatch)
    {
        isExactMatch = string.Equals(
            normalizedRuleFolder,
            normalizedFileParentFolder,
            StringComparison.OrdinalIgnoreCase);

        if (isExactMatch)
        {
            return true;
        }

        if (!includeSubfolders)
        {
            return false;
        }

        string ruleWithSep = EnsureTrailingSeparator(normalizedRuleFolder);
        string parentWithSep = EnsureTrailingSeparator(normalizedFileParentFolder);
        return parentWithSep.StartsWith(ruleWithSep, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAncestorOrEqual(string ancestorNormalizedFolder, string candidateNormalizedFolder)
    {
        string ancestorWithSep = EnsureTrailingSeparator(ancestorNormalizedFolder);
        string candidateWithSep = EnsureTrailingSeparator(candidateNormalizedFolder);
        return candidateWithSep.StartsWith(ancestorWithSep, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetCleanedFullPath(string? rawPath, bool isDirectory, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return false;
        }

        string cleaned = rawPath.Trim().Trim('"').Replace('/', '\\');
        if (cleaned.Length == 0)
        {
            return false;
        }

        if (cleaned.AsSpan().ContainsAny(InvalidCharacters) ||
            cleaned.Any(char.IsControl))
        {
            return false;
        }

        // Reject Windows device namespace syntax or stray colons outside drive prefix "X:"
        if (cleaned.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            cleaned.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return false;
        }

        int firstColon = cleaned.IndexOf(':');
        if (firstColon >= 0)
        {
            bool validDriveColon = firstColon == 1 && char.IsAsciiLetter(cleaned[0]) &&
                                   (cleaned.Length == 2 || cleaned[2] == '\\');
            if (!validDriveColon || cleaned.IndexOf(':', firstColon + 1) >= 0)
            {
                return false;
            }
        }

        if (!Path.IsPathFullyQualified(cleaned))
        {
            return false;
        }

        try
        {
            string resolved = Path.GetFullPath(cleaned);
            if (!isDirectory && resolved.EndsWith('\\'))
            {
                return false;
            }

            fullPath = resolved;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string StripTrailingDirectorySeparator(string fullPath)
    {
        if (fullPath.Length == 3 && fullPath[1] == ':' && fullPath[2] == '\\')
        {
            return fullPath;
        }

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            string trimmed = fullPath.TrimEnd('\\');
            string[] segments = trimmed.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length <= 2)
            {
                return @"\\" + string.Join('\\', segments);
            }

            return trimmed;
        }

        return fullPath.TrimEnd('\\');
    }
}
