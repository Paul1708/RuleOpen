using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace RuleOpen.Windows.Applications;

/// <summary>
/// Extracts crisp RGBA PNG icon bytes from Win32 binaries, icon resources, and packaged-app PRI references.
/// </summary>
public static class ShellIconExtractor
{
    public static byte[]? TryExtractIconPng(string? iconLocation, int iconIndex = 0, string? fallbackExecutablePath = null, int desiredSize = 48)
    {
        if (!string.IsNullOrWhiteSpace(iconLocation))
        {
            if (iconLocation.StartsWith("@{", StringComparison.Ordinal))
            {
                string? resolvedPri = TryResolveIndirectString(iconLocation);
                if (!string.IsNullOrWhiteSpace(resolvedPri) && File.Exists(resolvedPri))
                {
                    if (resolvedPri.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            return File.ReadAllBytes(resolvedPri);
                        }
                        catch
                        {
                            // Fall through to executable extraction.
                        }
                    }

                    byte[]? fromPriFile = TryExtractFromBinaryOrIconFile(resolvedPri, 0, desiredSize);
                    if (fromPriFile is not null)
                    {
                        return fromPriFile;
                    }
                }
            }
            else
            {
                (string parsedPath, int parsedIndex) = ParseIconLocation(iconLocation, iconIndex);
                byte[]? fromLocation = TryExtractFromBinaryOrIconFile(parsedPath, parsedIndex, desiredSize);
                if (fromLocation is not null)
                {
                    return fromLocation;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(fallbackExecutablePath) && File.Exists(fallbackExecutablePath))
        {
            return TryExtractFromBinaryOrIconFile(fallbackExecutablePath, 0, desiredSize);
        }

        return null;
    }

    public static unsafe byte[]? TryExtractFileIconPng(string filePath, int desiredSize = 32)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        if (filePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return File.ReadAllBytes(filePath);
            }
            catch
            {
                return null;
            }
        }

        byte[]? direct = TryExtractFromBinaryOrIconFile(filePath, 0, desiredSize);
        if (direct is not null)
        {
            return direct;
        }

        SHFILEINFOW shfi = default;
        nuint res = PInvoke.SHGetFileInfo(
            filePath,
            0,
            ref shfi,
            SHGFI_FLAGS.SHGFI_ICON | SHGFI_FLAGS.SHGFI_LARGEICON);

        if (res != 0 && !shfi.hIcon.IsNull)
        {
            try
            {
                return ConvertHIconToPng(shfi.hIcon);
            }
            finally
            {
                PInvoke.DestroyIcon(shfi.hIcon);
            }
        }

        return null;
    }

    internal static (string Path, int Index) ParseIconLocation(string raw, int defaultIndex = 0)
    {
        string trimmed = raw.Trim().Trim('"');
        int commaIndex = trimmed.LastIndexOf(',');
        if (commaIndex > 1 && int.TryParse(trimmed[(commaIndex + 1)..].Trim(), out int idx))
        {
            return (Environment.ExpandEnvironmentVariables(trimmed[..commaIndex].Trim().Trim('"')), idx);
        }

        return (Environment.ExpandEnvironmentVariables(trimmed), defaultIndex);
    }

    internal static string? TryResolveIndirectString(string source)
    {
        Span<char> buffer = stackalloc char[1024];
        HRESULT hr = PInvoke.SHLoadIndirectString(source, buffer);
        if (hr.Failed)
        {
            return null;
        }

        int nullIdx = buffer.IndexOf('\0');
        return nullIdx >= 0 ? new string(buffer[..nullIdx]) : new string(buffer);
    }

    private static unsafe byte[]? TryExtractFromBinaryOrIconFile(string path, int index, int desiredSize)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        Span<HICON> icons = stackalloc HICON[1];
        uint extracted = PInvoke.PrivateExtractIcons(path, index, desiredSize, desiredSize, icons, 0);
        if (extracted == 0 || icons[0].IsNull)
        {
            return null;
        }

        try
        {
            return ConvertHIconToPng(icons[0]);
        }
        finally
        {
            PInvoke.DestroyIcon(icons[0]);
        }
    }

    private static unsafe byte[]? ConvertHIconToPng(HICON hIcon)
    {
        ICONINFO iconInfo = default;
        if (!PInvoke.GetIconInfo(hIcon, &iconInfo))
        {
            return null;
        }

        try
        {
            HBITMAP colorOrMask = !iconInfo.hbmColor.IsNull ? iconInfo.hbmColor : iconInfo.hbmMask;
            if (colorOrMask.IsNull)
            {
                return null;
            }

            BITMAP bmpInfo = default;
            if (PInvoke.GetObject(colorOrMask, sizeof(BITMAP), &bmpInfo) == 0)
            {
                return null;
            }

            int width = bmpInfo.bmWidth;
            int height = !iconInfo.hbmColor.IsNull ? Math.Abs(bmpInfo.bmHeight) : Math.Abs(bmpInfo.bmHeight) / 2;
            if (width <= 0 || height <= 0 || width > 512 || height > 512)
            {
                return null;
            }

            HDC screenDc = PInvoke.GetDC(HWND.Null);
            if (screenDc.IsNull)
            {
                return null;
            }

            try
            {
                HDC memDc = PInvoke.CreateCompatibleDC(screenDc);
                if (memDc.IsNull)
                {
                    return null;
                }

                try
                {
                    byte[] bgra = new byte[width * height * 4];
                    BITMAPINFO bmi = default;
                    bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
                    bmi.bmiHeader.biWidth = width;
                    bmi.bmiHeader.biHeight = -height; // Top-down DIB
                    bmi.bmiHeader.biPlanes = 1;
                    bmi.bmiHeader.biBitCount = 32;
                    bmi.bmiHeader.biCompression = (uint)BI_COMPRESSION.BI_RGB;

                    fixed (byte* pBgra = bgra)
                    {
                        if (!iconInfo.hbmColor.IsNull)
                        {
                            int lines = PInvoke.GetDIBits(
                                memDc,
                                iconInfo.hbmColor,
                                0,
                                (uint)height,
                                pBgra,
                                &bmi,
                                DIB_USAGE.DIB_RGB_COLORS);

                            if (lines == 0)
                            {
                                return null;
                            }

                            // Legacy non-alpha icons leave every alpha byte at 0; recover opacity from hbmMask.
                            bool hasAlpha = false;
                            for (int i = 3; i < bgra.Length; i += 4)
                            {
                                if (bgra[i] != 0)
                                {
                                    hasAlpha = true;
                                    break;
                                }
                            }

                            if (!hasAlpha && !iconInfo.hbmMask.IsNull)
                            {
                                ApplyMaskAlpha(memDc, iconInfo.hbmMask, width, height, bgra);
                            }
                        }
                        else
                        {
                            return null;
                        }
                    }

                    return EncodeBgraAsPng(width, height, bgra);
                }
                finally
                {
                    PInvoke.DeleteDC(memDc);
                }
            }
            finally
            {
                _ = PInvoke.ReleaseDC(HWND.Null, screenDc);
            }
        }
        finally
        {
            if (!iconInfo.hbmColor.IsNull)
            {
                PInvoke.DeleteObject(iconInfo.hbmColor);
            }

            if (!iconInfo.hbmMask.IsNull)
            {
                PInvoke.DeleteObject(iconInfo.hbmMask);
            }
        }
    }

    private static unsafe void ApplyMaskAlpha(HDC memDc, HBITMAP hbmMask, int width, int height, byte[] bgra)
    {
        byte[] maskBgra = new byte[width * height * 4];
        BITMAPINFO maskBmi = default;
        maskBmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        maskBmi.bmiHeader.biWidth = width;
        maskBmi.bmiHeader.biHeight = -height;
        maskBmi.bmiHeader.biPlanes = 1;
        maskBmi.bmiHeader.biBitCount = 32;
        maskBmi.bmiHeader.biCompression = (uint)BI_COMPRESSION.BI_RGB;

        fixed (byte* pMask = maskBgra)
        {
            int lines = PInvoke.GetDIBits(memDc, hbmMask, 0, (uint)height, pMask, &maskBmi, DIB_USAGE.DIB_RGB_COLORS);
            if (lines == 0)
            {
                return;
            }
        }

        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i + 3] = maskBgra[i] == 0 ? (byte)255 : (byte)0;
        }
    }

    internal static byte[] EncodeBgraAsPng(int width, int height, ReadOnlySpan<byte> bgra)
    {
        // Convert top-down BGRA to PNG scanlines (filter byte 0 + RGBA).
        int stride = (width * 4) + 1;
        byte[] raw = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * stride;
            raw[rowStart] = 0;
            int srcRow = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int src = srcRow + (x * 4);
                int dst = rowStart + 1 + (x * 4);
                raw[dst] = bgra[src + 2];     // R
                raw[dst + 1] = bgra[src + 1]; // G
                raw[dst + 2] = bgra[src];     // B
                raw[dst + 3] = bgra[src + 3]; // A
            }
        }

        byte[] compressed;
        using (MemoryStream zlibOut = new())
        {
            using (ZLibStream zlib = new(zlibOut, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(raw, 0, raw.Length);
            }

            compressed = zlibOut.ToArray();
        }

        using MemoryStream png = new();
        // PNG Signature
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), height);
        ihdr[8] = 8;  // Bit depth
        ihdr[9] = 6;  // RGBA
        ihdr[10] = 0; // Compression
        ihdr[11] = 0; // Filter
        ihdr[12] = 0; // Interlace
        WriteChunk(png, "IHDR"u8, ihdr);
        WriteChunk(png, "IDAT"u8, compressed);
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> lenBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBytes, data.Length);
        stream.Write(lenBytes);
        stream.Write(type);
        stream.Write(data);

        uint crc = ComputeCrc32(type, data);
        BinaryPrimitives.WriteUInt32BigEndian(lenBytes, crc);
        stream.Write(lenBytes);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in type)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }
}
