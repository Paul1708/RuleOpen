using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using RuleOpen.Core.Interfaces;
using RuleOpen.Core.Models;
using RuleOpen.Windows.Applications;
using Windows.Storage.Streams;

namespace RuleOpen.Desktop.Services;

/// <summary>
/// Caches decoded application icons on disk/memory to avoid repeated Shell extraction on list updates.
/// </summary>
public sealed class IconCacheService(IApplicationDiscoveryService discoveryService)
{
    private readonly ConcurrentDictionary<string, byte[]?> _rawIconCache = new(StringComparer.OrdinalIgnoreCase);

    public void Seed(string applicationId, byte[]? pngBytes)
    {
        if (!string.IsNullOrWhiteSpace(applicationId) && pngBytes is { Length: > 0 })
        {
            _rawIconCache[applicationId] = pngBytes;
        }
    }

    public async Task<BitmapImage?> GetIconBitmapAsync(
        ApplicationTarget? target,
        string? extensionHint = null,
        CancellationToken cancellationToken = default)
    {
        if (target is null || string.IsNullOrWhiteSpace(target.ApplicationId))
        {
            return null;
        }

        byte[]? bytes = await GetIconPngBytesAsync(target, extensionHint, cancellationToken);
        return bytes is { Length: > 0 } ? await DecodePngAsync(bytes) : null;
    }

    public static async Task<BitmapImage?> DecodePngAsync(byte[]? pngBytes)
    {
        if (pngBytes is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            using InMemoryRandomAccessStream stream = new();
            await stream.WriteAsync(pngBytes.AsBuffer());
            stream.Seek(0);

            BitmapImage image = new();
            await image.SetSourceAsync(stream);
            return image;
        }
        catch
        {
            return null;
        }
    }

    private async Task<byte[]?> GetIconPngBytesAsync(
        ApplicationTarget target,
        string? extensionHint,
        CancellationToken cancellationToken)
    {
        if (_rawIconCache.TryGetValue(target.ApplicationId, out byte[]? cached))
        {
            return cached;
        }

        byte[]? bytes = null;
        if (!string.IsNullOrWhiteSpace(target.ExecutablePath) && File.Exists(target.ExecutablePath))
        {
            bytes = await discoveryService.ExtractExecutableIconAsync(target.ExecutablePath, cancellationToken);
        }

        if (bytes is null)
        {
            DiscoveredApplication? resolved = await discoveryService.ResolveTargetAsync(target, extensionHint, cancellationToken);
            bytes = resolved?.IconPngBytes;
        }

        _rawIconCache[target.ApplicationId] = bytes;
        return bytes;
    }
}
