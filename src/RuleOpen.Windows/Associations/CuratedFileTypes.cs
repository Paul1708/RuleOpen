using RuleOpen.Core.Rules;

namespace RuleOpen.Windows.Associations;

/// <summary>
/// Curated common file extensions and friendly names surfaced in the onboarding and picker UI.
/// RuleOpen also supports any user-entered extension dynamically.
/// </summary>
public static class CuratedFileTypes
{
    private static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "PDF Document",
        [".md"] = "Markdown Document",
        [".markdown"] = "Markdown Document",
        [".txt"] = "Text Document",
        [".log"] = "Log File",
        [".csv"] = "CSV Spreadsheet",
        [".json"] = "JSON File",
        [".xml"] = "XML Document",
        [".yaml"] = "YAML Document",
        [".yml"] = "YAML Document",
        [".html"] = "HTML Document",
        [".htm"] = "HTML Document",
        [".svg"] = "SVG Vector Graphic",
        [".png"] = "PNG Image",
        [".jpg"] = "JPEG Image",
        [".jpeg"] = "JPEG Image",
        [".webp"] = "WebP Image",
        [".gif"] = "GIF Image",
        [".docx"] = "Word Document",
        [".xlsx"] = "Excel Workbook",
        [".pptx"] = "PowerPoint Presentation",
        [".cs"] = "C# Source File",
        [".py"] = "Python Script",
        [".js"] = "JavaScript File",
        [".ts"] = "TypeScript File",
        [".mp3"] = "MP3 Audio",
        [".wav"] = "WAV Audio",
        [".mp4"] = "MP4 Video",
        [".mkv"] = "Matroska Video",
        [".zip"] = "ZIP Archive"
    };

    public static IReadOnlyList<string> DefaultSuggestedExtensions { get; } =
    [
        ".pdf", ".md", ".markdown", ".txt", ".json", ".xml", ".csv",
        ".html", ".png", ".jpg", ".svg", ".docx", ".xlsx", ".mp4", ".zip"
    ];

    /// <summary>
    /// Extensions declaratively registered in <c>AppxManifest.Store.xml</c> for Microsoft Store installs.
    /// </summary>
    public static IReadOnlyList<string> StoreDeclaredExtensions { get; } =
    [
        ".pdf", ".md", ".markdown", ".txt", ".rtf", ".doc", ".docx", ".odt",
        ".xls", ".xlsx", ".csv", ".ods", ".ppt", ".pptx", ".odp",
        ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".log",
        ".html", ".htm", ".css", ".js", ".ts", ".jsx", ".tsx", ".cs", ".py",
        ".cpp", ".c", ".h", ".hpp", ".rs", ".go", ".java", ".kt", ".ps1", ".sh", ".sql",
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".svg", ".tif", ".tiff", ".avif", ".ico",
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac",
        ".zip", ".7z", ".rar", ".tar", ".gz", ".epub", ".tex", ".rst", ".org", ".ics", ".eml"
    ];

    public static string GetFriendlyName(string extension)
    {
        if (!ExtensionNormalizer.TryNormalize(extension, out string norm))
        {
            return "File";
        }

        if (FriendlyNames.TryGetValue(norm, out string? name))
        {
            return name;
        }

        return $"{norm[1..].ToUpperInvariant()} File";
    }
}
