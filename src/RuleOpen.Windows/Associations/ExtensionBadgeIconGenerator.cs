using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RuleOpen.Core.Rules;
using RuleOpen.Core.Services;
using RuleOpen.Windows.Applications;

namespace RuleOpen.Windows.Associations;

/// <summary>
/// Generates high-DPI multi-frame Windows 11 Fluent document icons featuring the
/// iconic RuleOpen branching path behind crisp category-colored extension badges
/// (16, 20, 24, 32, 40, 48, 64, 96, 128, and 256 px PNG-in-ICO), and wraps MSIX PNG icons for Shell use.
/// </summary>
public static class ExtensionBadgeIconGenerator
{
    private const string IconFormatVersion = "v3";
    private const int PixelFormat32bppArgb = 0x0026200A;
    private const int PixelFormat32bppPArgb = 0x000E200B;
    private const int UnitPixel = 2;
    private const int WrapModeTileFlipXy = 3;
    private const int SmoothingModeAntiAlias = 4;
    private const int CompositingQualityHighQuality = 2;
    private const int PixelOffsetModeHighQuality = 2;
    private const int TextRenderingHintAntiAliasGridFit = 3;
    private const int TextRenderingHintAntiAlias = 4;
    private const int StringFormatFlagsNoWrap = 0x00001000;
    private const int StringFormatFlagsNoClip = 0x00004000;
    private const int FontStyleBold = 1;
    private const int StringAlignmentCenter = 1;
    private const int FillModeWinding = 1;
    private const int LineCapRound = 2;
    private const int LineJoinRound = 2;
    private const uint ImageLockModeRead = 0x0001;

    private static readonly int[] FrameSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
    private static readonly Lock RenderSync = new();

    private static bool _gdiplusInitialized;

    public static string? EnsureExtensionIcon(string normalizedExtension, string? outputDirectory = null)
    {
        if (!ExtensionNormalizer.TryNormalize(normalizedExtension, out string normExt))
        {
            return null;
        }

        string safeName = SanitizeFileToken(normExt);
        string dir = string.IsNullOrWhiteSpace(outputDirectory)
            ? ConfigPaths.GetDefaultIconsDirectory()
            : Path.GetFullPath(outputDirectory);
        string iconPath = Path.Combine(dir, $"ro_{IconFormatVersion}_{safeName}.ico");

        if (File.Exists(iconPath) && new FileInfo(iconPath).Length > 64)
        {
            return iconPath;
        }

        try
        {
            byte[] icoBytes = BuildMultiFrameIconBytes(normExt);
            WriteAtomicBytes(iconPath, icoBytes);
            return iconPath;
        }
        catch
        {
            return File.Exists(iconPath) ? iconPath : null;
        }
    }

    public static string? EnsureWrappedPngIcon(string pngFilePath, string? outputDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(pngFilePath) || !File.Exists(pngFilePath))
        {
            return null;
        }

        try
        {
            FileInfo info = new(pngFilePath);
            string cacheKey = $"{Path.GetFullPath(pngFilePath).ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey))).Substring(0, 12).ToLowerInvariant();
            string dir = string.IsNullOrWhiteSpace(outputDirectory)
                ? ConfigPaths.GetDefaultIconsDirectory()
                : Path.GetFullPath(outputDirectory);
            string icoPath = Path.Combine(dir, $"app_{hash}.ico");

            if (File.Exists(icoPath) && new FileInfo(icoPath).Length > 64)
            {
                return icoPath;
            }

            byte[] pngBytes = File.ReadAllBytes(pngFilePath);
            if (pngBytes.Length < 24 ||
                pngBytes[0] != 0x89 || pngBytes[1] != 0x50 || pngBytes[2] != 0x4E || pngBytes[3] != 0x47 ||
                pngBytes[12] != (byte)'I' || pngBytes[13] != (byte)'H' || pngBytes[14] != (byte)'D' || pngBytes[15] != (byte)'R')
            {
                return null;
            }

            int width = BinaryPrimitives.ReadInt32BigEndian(pngBytes.AsSpan(16, 4));
            int height = BinaryPrimitives.ReadInt32BigEndian(pngBytes.AsSpan(20, 4));
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            if (width > 256 || height > 256)
            {
                pngBytes = ResizePngTo256(pngFilePath);
                width = 256;
                height = 256;
            }

            byte[] icoBytes = PackPngsIntoIco([(width, height, pngBytes)]);
            WriteAtomicBytes(icoPath, icoBytes);
            return icoPath;
        }
        catch
        {
            return null;
        }
    }

    public static byte[] BuildMultiFrameIconBytes(string normalizedExtension)
    {
        string label = FormatBadgeLabel(normalizedExtension);
        (uint TopColor, uint BottomColor) accent = ResolveCategoryColors(normalizedExtension);

        List<(int Width, int Height, byte[] Png)> frames = new(FrameSizes.Length);
        lock (RenderSync)
        {
            EnsureGdiplusStarted();
            foreach (int size in FrameSizes)
            {
                frames.Add((size, size, RenderPngFrame(size, label, accent.TopColor, accent.BottomColor)));
            }
        }

        return PackPngsIntoIco(frames);
    }

    internal static string FormatBadgeLabel(string normalizedExtension)
    {
        string raw = normalizedExtension.Trim().TrimStart('.').ToUpperInvariant();
        if (string.IsNullOrEmpty(raw))
        {
            return "FILE";
        }

        return raw switch
        {
            "MARKDOWN" => "MD",
            "YAML" => "YML",
            "JPEG" => "JPG",
            "TIFF" => "TIF",
            "TORRENT" => "TOR",
            "CONFIG" => "CFG",
            "POWERSHELL" => "PS1",
            _ when raw.Length > 5 => raw[..5],
            _ => raw
        };
    }

    private static void WriteAtomicBytes(string targetPath, byte[] bytes)
    {
        string dir = Path.GetDirectoryName(targetPath) ?? ".";
        Directory.CreateDirectory(dir);

        string tempPath = Path.Combine(dir, $".{Path.GetFileName(targetPath)}.tmp.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}");
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private static byte[] ResizePngTo256(string pngPath)
    {
        const int targetSize = 256;
        lock (RenderSync)
        {
            EnsureGdiplusStarted();
            nint srcImage = 0;
            nint dstBitmap = 0;
            nint graphics = 0;
            try
            {
                CheckGdip(GdipLoadImageFromFile(pngPath, out srcImage));
                CheckGdip(GdipCreateBitmapFromScan0(targetSize, targetSize, 0, PixelFormat32bppPArgb, 0, out dstBitmap));
                CheckGdip(GdipGetImageGraphicsContext(dstBitmap, out graphics));
                CheckGdip(GdipGraphicsClear(graphics, 0x00000000u));
                CheckGdip(GdipSetSmoothingMode(graphics, SmoothingModeAntiAlias));
                CheckGdip(GdipDrawImageRect(graphics, srcImage, 0f, 0f, targetSize, targetSize));
                return ExtractPngFromBitmap(dstBitmap, targetSize);
            }
            finally
            {
                if (graphics != 0) _ = GdipDeleteGraphics(graphics);
                if (dstBitmap != 0) _ = GdipDisposeImage(dstBitmap);
                if (srcImage != 0) _ = GdipDisposeImage(srcImage);
            }
        }
    }

    private static (uint TopColor, uint BottomColor) ResolveCategoryColors(string normalizedExtension)
    {
        return normalizedExtension.ToLowerInvariant() switch
        {
            ".pdf" => (0xFFD13438u, 0xFFA4262Cu),
            ".doc" or ".docx" or ".rtf" or ".odt" => (0xFF185ABDu, 0xFF103F88u),
            ".xls" or ".xlsx" or ".csv" or ".ods" => (0xFF107C41u, 0xFF0A562Du),
            ".ppt" or ".pptx" or ".odp" => (0xFFC43E1Cu, 0xFF932D12u),
            ".txt" or ".log" or ".ini" or ".cfg" or ".conf" => (0xFF475569u, 0xFF334155u),
            ".md" or ".markdown" or ".rst" => (0xFF5B5FC7u, 0xFF44479Au),
            ".json" or ".xml" or ".yaml" or ".yml" or ".toml" => (0xFFC17900u, 0xFF945A00u),
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" or ".svg" or ".ico" or ".tif" or ".tiff" or ".avif" => (0xFF008272u, 0xFF005C50u),
            ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" or ".wmv" => (0xFF7719AAu, 0xFF55107Au),
            ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" or ".aac" => (0xFFB4009Eu, 0xFF840073u),
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => (0xFF8E562Eu, 0xFF663D20u),
            ".html" or ".htm" or ".css" or ".js" or ".ts" or ".cs" or ".py" or ".cpp" or ".c" or ".h" or ".rs" or ".go" or ".java" or ".ps1" or ".sh" or ".sql" => (0xFF0078D4u, 0xFF005A9Eu),
            _ => PickFallbackPaletteColor(normalizedExtension)
        };
    }

    private static (uint TopColor, uint BottomColor) PickFallbackPaletteColor(string ext)
    {
        (uint Top, uint Bottom)[] palette =
        [
            (0xFF0078D4u, 0xFF005A9Eu),
            (0xFF038387u, 0xFF025E61u),
            (0xFF4F6BEDu, 0xFF384DB8u),
            (0xFF8764B8u, 0xFF64478Du),
            (0xFF498205u, 0xFF345E03u),
            (0xFFCA5010u, 0xFF973A0Au)
        ];

        uint hash = 2166136261u;
        foreach (char c in ext.ToUpperInvariant())
        {
            hash = (hash ^ c) * 16777619u;
        }

        return palette[hash % (uint)palette.Length];
    }

    private static string SanitizeFileToken(string normalizedExtension)
    {
        string raw = normalizedExtension.TrimStart('.');
        Span<char> buf = stackalloc char[raw.Length];
        int count = 0;
        foreach (char ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            {
                buf[count++] = char.ToLowerInvariant(ch);
            }
            else
            {
                buf[count++] = '_';
            }
        }

        return count > 0 ? new string(buf[..count]) : "file";
    }

    private static byte[] PackPngsIntoIco(List<(int Width, int Height, byte[] Png)> frames)
    {
        int count = frames.Count;
        int headerSize = 6 + (16 * count);
        int totalSize = headerSize + frames.Sum(f => f.Png.Length);
        byte[] ico = new byte[totalSize];

        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(0, 2), 0); // Reserved
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2, 2), 1); // Type 1 = Icon
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4, 2), (ushort)count);

        int offset = headerSize;
        for (int i = 0; i < count; i++)
        {
            int entryPos = 6 + (i * 16);
            (int w, int h, byte[] png) = frames[i];

            ico[entryPos] = w >= 256 ? (byte)0 : (byte)w;
            ico[entryPos + 1] = h >= 256 ? (byte)0 : (byte)h;
            ico[entryPos + 2] = 0;
            ico[entryPos + 3] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(entryPos + 4, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(entryPos + 6, 2), 32);
            BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(entryPos + 8, 4), (uint)png.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(entryPos + 12, 4), (uint)offset);

            Buffer.BlockCopy(png, 0, ico, offset, png.Length);
            offset += png.Length;
        }

        return ico;
    }

    private static byte[] RenderPngFrame(int size, string label, uint accentTop, uint accentBottom)
    {
        // All coordinates live in a 256x256 canonical RuleOpen icon canvas.
        float s = size / 256.0f;
        bool compact = size <= 20;

        nint bitmap = 0;
        nint graphics = 0;

        try
        {
            CheckGdip(GdipCreateBitmapFromScan0(size, size, 0, PixelFormat32bppPArgb, 0, out bitmap));
            CheckGdip(GdipGetImageGraphicsContext(bitmap, out graphics));
            CheckGdip(GdipGraphicsClear(graphics, 0x00000000u));
            CheckGdip(GdipSetSmoothingMode(graphics, SmoothingModeAntiAlias));
            CheckGdip(GdipSetPixelOffsetMode(graphics, PixelOffsetModeHighQuality));
            CheckGdip(GdipSetCompositingQuality(graphics, CompositingQualityHighQuality));
            CheckGdip(GdipSetTextRenderingHint(graphics, size is >= 24 and <= 64 ? TextRenderingHintAntiAliasGridFit : TextRenderingHintAntiAlias));

            // 1. Back Layer: Document sheet on upper-left (14..154, 10..234).
            float pLeft = 14f * s;
            float pRight = 154f * s;
            float pTop = 10f * s;
            float pBottom = 234f * s;
            float corner = MathF.Max(1.4f, 18f * s);
            float fold = 40f * s;
            float borderWidth = compact ? MathF.Max(1.0f, 8f * s) : MathF.Max(1.2f, 4f * s);

            if (!compact)
            {
                FillDocumentPath(graphics, pLeft, pTop + 5f * s, pRight, pBottom + 5f * s, corner, fold, 0x1E0F172Au);
            }

            FillAndStrokeDocumentSheet(
                graphics,
                pLeft,
                pTop,
                pRight,
                pBottom,
                corner,
                fold,
                0xFFFFFFFFu,
                0xFFE5ECF5u,
                0xFF94A6BCu,
                borderWidth);

            DrawFoldFlap(graphics, pRight, pTop, fold, 0xFFD4DFECu, 0xFF94A6BCu, borderWidth);

            if (!compact)
            {
                DrawRoundedBar(graphics, 32f * s, 38f * s, 44f * s, MathF.Max(1.4f, 9f * s), 0xFFB8C7D9u);
                DrawRoundedBar(graphics, 32f * s, 55f * s, 68f * s, MathF.Max(1.4f, 9f * s), 0xFFCBD7E6u);
            }
            else
            {
                DrawRoundedBar(graphics, 32f * s, 38f * s, 48f * s, MathF.Max(1.2f, 12f * s), 0xFFACC0D6u);
            }

            // 2. Middle Layer: Iconic RuleOpen branching path + secondary target square.
            float nodeX = 52f * s;
            float nodeY = 98f * s;
            float secWidth = compact ? MathF.Max(1.6f, 16f * s) : MathF.Max(1.8f, 11.5f * s);
            DrawBezierPath(
                graphics,
                86f * s, nodeY,
                122f * s, nodeY,
                124f * s, 46f * s,
                176f * s, 46f * s,
                0xFF7EC0F5u,
                secWidth);

            // Secondary app square at top-right.
            float secTileX = 174f * s;
            float secTileY = 22f * s;
            float secTileSize = 48f * s;
            float secTileRadius = MathF.Max(1.5f, 12f * s);
            FillGradientRoundedRect(
                graphics,
                secTileX,
                secTileY,
                secTileX + secTileSize,
                secTileY + secTileSize,
                secTileRadius,
                0xFFC8E3FAu,
                0xFF8BC4F5u);
            StrokeRoundedRect(
                graphics,
                secTileX,
                secTileY,
                secTileX + secTileSize,
                secTileY + secTileSize,
                secTileRadius,
                0xFF62B0EFu,
                MathF.Max(1.0f, 3f * s));

            // Origin white halo behind branching stem (skip at <=20px).
            if (!compact)
            {
                float haloR = 18f * s;
                FillCircle(graphics, nodeX, nodeY, haloR, 0xFFFFFFFFu);
            }

            // Bold primary RuleOpen-blue branch routing cleanly into the file badge.
            float priWidth = compact ? MathF.Max(2.0f, 20f * s) : MathF.Max(2.4f, 16f * s);
            DrawPrimaryRouteStemAndCurve(
                graphics,
                nodeX, nodeY,
                98f * s, nodeY,
                128f * s, nodeY,
                132f * s, 136f * s,
                172f * s, 136f * s,
                0xFF2899F5u,
                0xFF0067C0u,
                priWidth);

            // Solid origin node dot on top of path start.
            float nodeR = compact ? 14f * s : 13f * s;
            FillCircle(graphics, nodeX, nodeY, nodeR, 0xFF0078D4u);

            // 3. Top Layer: Bigger, high-contrast category-colored extension badge floating above the path.
            float pillLeft = (compact ? 8f : 24f) * s;
            float pillTop = (compact ? 102f : 124f) * s;
            float pillRight = (compact ? 252f : 248f) * s;
            float pillBottom = (compact ? 248f : 244f) * s;
            float pillRadius = MathF.Max(compact ? 2.0f : 2.2f, 22f * s);

            if (!compact)
            {
                FillRoundedRect(graphics, pillLeft, pillTop + 5f * s, pillRight, pillBottom + 5f * s, pillRadius, 0x34000000u);
            }

            // Clean white separation halo where the badge floats above the blue path and document edge.
            StrokeRoundedRect(
                graphics,
                pillLeft,
                pillTop,
                pillRight,
                pillBottom,
                pillRadius,
                0xF5FFFFFFu,
                compact ? MathF.Max(1.2f, 9f * s) : MathF.Max(1.8f, 8f * s));

            FillGradientRoundedRect(graphics, pillLeft, pillTop, pillRight, pillBottom, pillRadius, accentTop, accentBottom);
            StrokeRoundedRect(
                graphics,
                pillLeft,
                pillTop,
                pillRight,
                pillBottom,
                pillRadius,
                0x30000000u,
                MathF.Max(0.8f, 2.5f * s));

            DrawBadgeLabel(graphics, label, pillLeft, pillTop, pillRight - pillLeft, pillBottom - pillTop, size);

            return ExtractPngFromBitmap(bitmap, size);
        }
        finally
        {
            if (graphics != 0) _ = GdipDeleteGraphics(graphics);
            if (bitmap != 0) _ = GdipDisposeImage(bitmap);
        }
    }

    private static void DrawBezierPath(
        nint graphics,
        float x1, float y1,
        float x2, float y2,
        float x3, float y3,
        float x4, float y4,
        uint color,
        float width)
    {
        nint path = 0;
        nint pen = 0;
        try
        {
            CheckGdip(GdipCreatePath(FillModeWinding, out path));
            CheckGdip(GdipAddPathBezier(path, x1, y1, x2, y2, x3, y3, x4, y4));
            CheckGdip(GdipCreatePen1(color, width, UnitPixel, out pen));
            CheckGdip(GdipSetPenStartCap(pen, LineCapRound));
            CheckGdip(GdipSetPenEndCap(pen, LineCapRound));
            CheckGdip(GdipSetPenLineJoin(pen, LineJoinRound));
            CheckGdip(GdipDrawPath(graphics, pen, path));
        }
        finally
        {
            if (pen != 0) _ = GdipDeletePen(pen);
            if (path != 0) _ = GdipDeletePath(path);
        }
    }

    private static void DrawPrimaryRouteStemAndCurve(
        nint graphics,
        float sx, float sy,
        float mx, float my,
        float c1x, float c1y,
        float c2x, float c2y,
        float ex, float ey,
        uint startColor,
        uint endColor,
        float width)
    {
        nint path = 0;
        nint brush = 0;
        nint pen = 0;
        try
        {
            CheckGdip(GdipCreatePath(FillModeWinding, out path));
            CheckGdip(GdipAddPathLine(path, sx, sy, mx, my));
            CheckGdip(GdipAddPathBezier(path, mx, my, c1x, c1y, c2x, c2y, ex, ey));

            GpPointF p1 = new() { X = sx, Y = sy };
            GpPointF p2 = new() { X = ex, Y = ey };
            CheckGdip(GdipCreateLineBrush(ref p1, ref p2, startColor, endColor, WrapModeTileFlipXy, out brush));
            CheckGdip(GdipCreatePen2(brush, width, UnitPixel, out pen));
            CheckGdip(GdipSetPenStartCap(pen, LineCapRound));
            CheckGdip(GdipSetPenEndCap(pen, LineCapRound));
            CheckGdip(GdipSetPenLineJoin(pen, LineJoinRound));
            CheckGdip(GdipDrawPath(graphics, pen, path));
        }
        finally
        {
            if (pen != 0) _ = GdipDeletePen(pen);
            if (brush != 0) _ = GdipDeleteBrush(brush);
            if (path != 0) _ = GdipDeletePath(path);
        }
    }

    private static void FillCircle(nint graphics, float cx, float cy, float radius, uint color)
    {
        nint brush = 0;
        try
        {
            CheckGdip(GdipCreateSolidFill(color, out brush));
            CheckGdip(GdipFillEllipse(graphics, brush, cx - radius, cy - radius, radius * 2f, radius * 2f));
        }
        finally
        {
            if (brush != 0) _ = GdipDeleteBrush(brush);
        }
    }

    private static void FillDocumentPath(nint graphics, float l, float t, float r, float b, float cr, float fold, uint color)
    {
        nint path = 0;
        nint brush = 0;
        try
        {
            path = CreateDocumentPath(l, t, r, b, cr, fold);
            CheckGdip(GdipCreateSolidFill(color, out brush));
            CheckGdip(GdipFillPath(graphics, brush, path));
        }
        finally
        {
            if (brush != 0) _ = GdipDeleteBrush(brush);
            if (path != 0) _ = GdipDeletePath(path);
        }
    }

    private static void FillAndStrokeDocumentSheet(
        nint graphics,
        float l,
        float t,
        float r,
        float b,
        float cr,
        float fold,
        uint topColor,
        uint bottomColor,
        uint borderColor,
        float borderWidth)
    {
        nint path = 0;
        nint brush = 0;
        nint pen = 0;
        try
        {
            path = CreateDocumentPath(l, t, r, b, cr, fold);
            GpPointF p1 = new() { X = l, Y = t };
            GpPointF p2 = new() { X = l, Y = b };
            CheckGdip(GdipCreateLineBrush(ref p1, ref p2, topColor, bottomColor, WrapModeTileFlipXy, out brush));
            CheckGdip(GdipFillPath(graphics, brush, path));

            CheckGdip(GdipCreatePen1(borderColor, borderWidth, UnitPixel, out pen));
            CheckGdip(GdipDrawPath(graphics, pen, path));
        }
        finally
        {
            if (pen != 0) _ = GdipDeletePen(pen);
            if (brush != 0) _ = GdipDeleteBrush(brush);
            if (path != 0) _ = GdipDeletePath(path);
        }
    }

    private static nint CreateDocumentPath(float l, float t, float r, float b, float cr, float fold)
    {
        CheckGdip(GdipCreatePath(FillModeWinding, out nint path));
        float d = cr * 2f;

        CheckGdip(GdipAddPathLine(path, l + cr, t, r - fold, t));
        CheckGdip(GdipAddPathLine(path, r - fold, t, r, t + fold));
        CheckGdip(GdipAddPathLine(path, r, t + fold, r, b - cr));
        CheckGdip(GdipAddPathArc(path, r - d, b - d, d, d, 0f, 90f));
        CheckGdip(GdipAddPathLine(path, r - cr, b, l + cr, b));
        CheckGdip(GdipAddPathArc(path, l, b - d, d, d, 90f, 90f));
        CheckGdip(GdipAddPathLine(path, l, b - cr, l, t + cr));
        CheckGdip(GdipAddPathArc(path, l, t, d, d, 180f, 90f));
        CheckGdip(GdipClosePathFigure(path));
        return path;
    }

    private static void DrawFoldFlap(nint graphics, float r, float t, float fold, uint fillColor, uint strokeColor, float strokeWidth)
    {
        nint path = 0;
        nint brush = 0;
        nint pen = 0;
        try
        {
            CheckGdip(GdipCreatePath(FillModeWinding, out path));
            float xFold = r - fold;
            float yFold = t + fold;
            CheckGdip(GdipAddPathLine(path, xFold, t, r, yFold));
            CheckGdip(GdipAddPathLine(path, r, yFold, xFold, yFold));
            CheckGdip(GdipAddPathLine(path, xFold, yFold, xFold, t));
            CheckGdip(GdipClosePathFigure(path));

            CheckGdip(GdipCreateSolidFill(fillColor, out brush));
            CheckGdip(GdipFillPath(graphics, brush, path));

            CheckGdip(GdipCreatePen1(strokeColor, strokeWidth, UnitPixel, out pen));
            CheckGdip(GdipSetPenLineJoin(pen, LineJoinRound));
            CheckGdip(GdipDrawPath(graphics, pen, path));
        }
        finally
        {
            if (pen != 0) _ = GdipDeletePen(pen);
            if (brush != 0) _ = GdipDeleteBrush(brush);
            if (path != 0) _ = GdipDeletePath(path);
        }
    }

    private static void DrawRoundedBar(nint graphics, float x, float y, float w, float h, uint color)
    {
        FillRoundedRect(graphics, x, y, x + w, y + h, h * 0.5f, color);
    }

    private static nint CreateRoundedRectPath(float l, float t, float r, float b, float cr)
    {
        CheckGdip(GdipCreatePath(FillModeWinding, out nint path));
        float d = MathF.Min(cr * 2f, MathF.Min(r - l, b - t));
        float rad = d * 0.5f;

        CheckGdip(GdipAddPathArc(path, l, t, d, d, 180f, 90f));
        CheckGdip(GdipAddPathLine(path, l + rad, t, r - rad, t));
        CheckGdip(GdipAddPathArc(path, r - d, t, d, d, 270f, 90f));
        CheckGdip(GdipAddPathLine(path, r, t + rad, r, b - rad));
        CheckGdip(GdipAddPathArc(path, r - d, b - d, d, d, 0f, 90f));
        CheckGdip(GdipAddPathLine(path, r - rad, b, l + rad, b));
        CheckGdip(GdipAddPathArc(path, l, b - d, d, d, 90f, 90f));
        CheckGdip(GdipAddPathLine(path, l, b - rad, l, t + rad));
        CheckGdip(GdipClosePathFigure(path));
        return path;
    }

    private static void FillRoundedRect(nint graphics, float l, float t, float r, float b, float cr, uint color)
    {
        nint path = 0;
        nint brush = 0;
        try
        {
            path = CreateRoundedRectPath(l, t, r, b, cr);
            CheckGdip(GdipCreateSolidFill(color, out brush));
            CheckGdip(GdipFillPath(graphics, brush, path));
        }
        finally
        {
            if (brush != 0) _ = GdipDeleteBrush(brush);
            if (path != 0) _ = GdipDeletePath(path);
        }
    }

    private static void FillGradientRoundedRect(nint graphics, float l, float t, float r, float b, float cr, uint topColor, uint bottomColor)
    {
        nint path = 0;
        nint brush = 0;
        try
        {
            path = CreateRoundedRectPath(l, t, r, b, cr);
            GpPointF p1 = new() { X = l, Y = t };
            GpPointF p2 = new() { X = l, Y = b };
            CheckGdip(GdipCreateLineBrush(ref p1, ref p2, topColor, bottomColor, WrapModeTileFlipXy, out brush));
            CheckGdip(GdipFillPath(graphics, brush, path));
        }
        finally
        {
            if (brush != 0) _ = GdipDeleteBrush(brush);
            if (path != 0) _ = GdipDeletePath(path);
        }
    }

    private static void StrokeRoundedRect(nint graphics, float l, float t, float r, float b, float cr, uint borderColor, float width)
    {
        nint path = 0;
        nint pen = 0;
        try
        {
            path = CreateRoundedRectPath(l, t, r, b, cr);
            CheckGdip(GdipCreatePen1(borderColor, width, UnitPixel, out pen));
            CheckGdip(GdipSetPenLineJoin(pen, LineJoinRound));
            CheckGdip(GdipDrawPath(graphics, pen, path));
        }
        finally
        {
            if (pen != 0) _ = GdipDeletePen(pen);
            if (path != 0) _ = GdipDeletePath(path);
        }
    }

    private static void DrawBadgeLabel(nint graphics, string label, float x, float y, float width, float height, int iconSize)
    {
        float heightScale = label.Length switch
        {
            <= 2 => 0.74f,
            3 => 0.68f,
            4 => 0.56f,
            _ => 0.46f
        };

        float widthBudget = (width * 0.88f) / MathF.Max(1f, label.Length * 0.66f);
        float emSize = MathF.Max(3.5f, MathF.Min(height * heightScale, widthBudget));

        nint family = 0;
        nint font = 0;
        nint format = 0;
        nint shadowBrush = 0;
        nint brush = 0;

        try
        {
            if (GdipCreateFontFamilyFromName("Segoe UI", 0, out family) != 0 || family == 0)
            {
                CheckGdip(GdipCreateFontFamilyFromName("Arial", 0, out family));
            }

            CheckGdip(GdipCreateFont(family, emSize, FontStyleBold, UnitPixel, out font));
            CheckGdip(GdipCreateStringFormat(StringFormatFlagsNoWrap | StringFormatFlagsNoClip, 0, out format));
            CheckGdip(GdipSetStringFormatAlign(format, StringAlignmentCenter));
            CheckGdip(GdipSetStringFormatLineAlign(format, StringAlignmentCenter));
            CheckGdip(GdipCreateSolidFill(0xFFFFFFFFu, out brush));

            GpRectF layout = new()
            {
                X = x - width * 0.25f,
                Y = y + height * 0.03f,
                Width = width * 1.50f,
                Height = height
            };

            if (iconSize >= 32)
            {
                CheckGdip(GdipCreateSolidFill(0x50000000u, out shadowBrush));
                float dy = MathF.Max(1f, iconSize * 0.01f);
                GpRectF shadowLayout = new()
                {
                    X = layout.X,
                    Y = layout.Y + dy,
                    Width = layout.Width,
                    Height = layout.Height
                };
                CheckGdip(GdipDrawString(graphics, label, label.Length, font, ref shadowLayout, format, shadowBrush));
            }

            CheckGdip(GdipDrawString(graphics, label, label.Length, font, ref layout, format, brush));
        }
        finally
        {
            if (shadowBrush != 0) _ = GdipDeleteBrush(shadowBrush);
            if (brush != 0) _ = GdipDeleteBrush(brush);
            if (format != 0) _ = GdipDeleteStringFormat(format);
            if (font != 0) _ = GdipDeleteFont(font);
            if (family != 0) _ = GdipDeleteFontFamily(family);
        }
    }

    private static unsafe byte[] ExtractPngFromBitmap(nint bitmap, int size)
    {
        GpRect rect = new() { X = 0, Y = 0, Width = size, Height = size };
        GpBitmapData data = default;
        CheckGdip(GdipBitmapLockBits(bitmap, ref rect, ImageLockModeRead, PixelFormat32bppArgb, ref data));

        try
        {
            byte[] bgra = new byte[size * size * 4];
            int rowBytes = size * 4;
            for (int y = 0; y < size; y++)
            {
                byte* src = (byte*)data.Scan0 + (y * data.Stride);
                Marshal.Copy((nint)src, bgra, y * rowBytes, rowBytes);
            }

            return ShellIconExtractor.EncodeBgraAsPng(size, size, bgra);
        }
        finally
        {
            _ = GdipBitmapUnlockBits(bitmap, ref data);
        }
    }

    private static void EnsureGdiplusStarted()
    {
        if (_gdiplusInitialized)
        {
            return;
        }

        GdiplusStartupInput input = new() { GdiplusVersion = 1 };
        CheckGdip(GdiplusStartup(out _, ref input, 0));
        _gdiplusInitialized = true;
    }

    private static void CheckGdip(int status)
    {
        if (status != 0)
        {
            throw new InvalidOperationException($"GDI+ operation failed with status {status}.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GdiplusStartupInput
    {
        public uint GdiplusVersion;
        public nint DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpPointF
    {
        public float X;
        public float Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpRect
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpRectF
    {
        public float X;
        public float Y;
        public float Width;
        public float Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpBitmapData
    {
        public uint Width;
        public uint Height;
        public int Stride;
        public int PixelFormat;
        public nint Scan0;
        public nuint Reserved;
    }

    [DllImport("gdiplus.dll")]
    private static extern int GdiplusStartup(out nuint token, ref GdiplusStartupInput input, nint output);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    private static extern int GdipLoadImageFromFile(string filename, out nint image);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDrawImageRect(nint graphics, nint image, float x, float y, float width, float height);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, nint scan0, out nint bitmap);

    [DllImport("gdiplus.dll")]
    private static extern int GdipGetImageGraphicsContext(nint image, out nint graphics);

    [DllImport("gdiplus.dll")]
    private static extern int GdipGraphicsClear(nint graphics, uint color);

    [DllImport("gdiplus.dll")]
    private static extern int GdipSetSmoothingMode(nint graphics, int smoothingMode);

    [DllImport("gdiplus.dll")]
    private static extern int GdipSetCompositingQuality(nint graphics, int compositingQuality);

    [DllImport("gdiplus.dll")]
    private static extern int GdipSetPixelOffsetMode(nint graphics, int pixelOffsetMode);

    [DllImport("gdiplus.dll")]
    private static extern int GdipSetTextRenderingHint(nint graphics, int mode);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreatePath(int fillMode, out nint path);

    [DllImport("gdiplus.dll")]
    private static extern int GdipAddPathLine(nint path, float x1, float y1, float x2, float y2);

    [DllImport("gdiplus.dll")]
    private static extern int GdipAddPathBezier(nint path, float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4);

    [DllImport("gdiplus.dll")]
    private static extern int GdipAddPathArc(nint path, float x, float y, float width, float height, float startAngle, float sweepAngle);

    [DllImport("gdiplus.dll")]
    private static extern int GdipClosePathFigure(nint path);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeletePath(nint path);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreateSolidFill(uint color, out nint brush);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreateLineBrush(ref GpPointF point1, ref GpPointF point2, uint color1, uint color2, int wrapMode, out nint brush);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeleteBrush(nint brush);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreatePen1(uint color, float width, int unit, out nint pen);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreatePen2(nint brush, float width, int unit, out nint pen);

    [DllImport("gdiplus.dll")]
    private static extern int GdipSetPenStartCap(nint pen, int startCap);

    [DllImport("gdiplus.dll")]
    private static extern int GdipSetPenEndCap(nint pen, int endCap);

    [DllImport("gdiplus.dll")]
    private static extern int GdipSetPenLineJoin(nint pen, int lineJoin);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDrawPath(nint graphics, nint pen, nint path);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeletePen(nint pen);

    [DllImport("gdiplus.dll")]
    private static extern int GdipFillPath(nint graphics, nint brush, nint path);

    [DllImport("gdiplus.dll")]
    private static extern int GdipFillEllipse(nint graphics, nint brush, float x, float y, float width, float height);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    private static extern int GdipCreateFontFamilyFromName(string name, nint fontCollection, out nint fontFamily);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeleteFontFamily(nint fontFamily);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreateFont(nint fontFamily, float emSize, int style, int unit, out nint font);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeleteFont(nint font);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreateStringFormat(int formatAttributes, ushort language, out nint format);

    [DllImport("gdiplus.dll")]
    private static extern int GdipSetStringFormatAlign(nint format, int align);

    [DllImport("gdiplus.dll")]
    private static extern int GdipSetStringFormatLineAlign(nint format, int align);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeleteStringFormat(nint format);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    private static extern int GdipDrawString(nint graphics, string text, int length, nint font, ref GpRectF layoutRect, nint stringFormat, nint brush);

    [DllImport("gdiplus.dll")]
    private static extern int GdipBitmapLockBits(nint bitmap, ref GpRect rect, uint flags, int format, ref GpBitmapData lockedBitmapData);

    [DllImport("gdiplus.dll")]
    private static extern int GdipBitmapUnlockBits(nint bitmap, ref GpBitmapData lockedBitmapData);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeleteGraphics(nint graphics);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDisposeImage(nint image);
}
