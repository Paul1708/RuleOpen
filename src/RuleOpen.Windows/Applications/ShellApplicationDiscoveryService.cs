using System.Runtime.InteropServices;
using RuleOpen.Core.Interfaces;
using RuleOpen.Core.Models;
using RuleOpen.Core.Rules;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace RuleOpen.Windows.Applications;

/// <summary>
/// Discovers compatible Windows applications via official Shell association APIs
/// (<c>SHAssocEnumHandlers</c> and <c>AssocQueryString</c>), excluding RuleOpen itself
/// and system stub launchers.
/// </summary>
public sealed class ShellApplicationDiscoveryService : IApplicationDiscoveryService
{
    private static readonly HashSet<string> NoisySystemExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "openwith.exe",
        "rundll32.exe",
        "shell32.dll",
        "dllhost.exe",
        "launcher.exe",
        "cmd.exe",
        "wscript.exe",
        "cscript.exe"
    };

    public Task<IReadOnlyList<DiscoveredApplication>> GetHandlersForExtensionAsync(
        string extension,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ExtensionNormalizer.TryNormalize(extension, out string normalizedExt))
        {
            return Task.FromResult<IReadOnlyList<DiscoveredApplication>>([]);
        }

        return Task.Run<IReadOnlyList<DiscoveredApplication>>(
            () => StaThreadRunner.Run(() => EnumerateHandlersCore(normalizedExt, includeIcons: true, cancellationToken)),
            cancellationToken);
    }

    public async Task<DiscoveredApplication?> ResolveTargetAsync(
        ApplicationTarget target,
        string? extensionHint = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.Kind == ApplicationTargetKind.CustomExecutable)
        {
            string? exe = target.ExecutablePath ?? target.ApplicationId;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                return null;
            }

            byte[]? iconBytes = await ExtractExecutableIconAsync(exe, cancellationToken).ConfigureAwait(false);
            return new DiscoveredApplication
            {
                Id = exe,
                DisplayName = target.DisplayName,
                Kind = ApplicationTargetKind.CustomExecutable,
                ExecutablePath = exe,
                IconLocation = exe,
                IconPngBytes = iconBytes
            };
        }

        if (!string.IsNullOrWhiteSpace(extensionHint) &&
            ExtensionNormalizer.TryNormalize(extensionHint, out string normExt))
        {
            IReadOnlyList<DiscoveredApplication> handlers = await GetHandlersForExtensionAsync(normExt, cancellationToken)
                .ConfigureAwait(false);

            DiscoveredApplication? match = handlers.FirstOrDefault(h => MatchesTarget(h, target));
            if (match is not null)
            {
                return match;
            }
        }

        if (!string.IsNullOrWhiteSpace(target.ExecutablePath) && File.Exists(target.ExecutablePath))
        {
            byte[]? iconBytes = await ExtractExecutableIconAsync(target.ExecutablePath, cancellationToken)
                .ConfigureAwait(false);

            return new DiscoveredApplication
            {
                Id = target.ApplicationId,
                DisplayName = target.DisplayName,
                Kind = target.Kind,
                ExecutablePath = target.ExecutablePath,
                ProgId = target.ProgId,
                AppUserModelId = target.AppUserModelId,
                IconLocation = target.ExecutablePath,
                IconPngBytes = iconBytes
            };
        }

        return null;
    }

    public Task<byte[]?> ExtractExecutableIconAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => ShellIconExtractor.TryExtractFileIconPng(executablePath, 48),
            cancellationToken);
    }

    internal static unsafe IReadOnlyList<DiscoveredApplication> EnumerateHandlersCore(
        string normalizedExtension,
        bool includeIcons,
        CancellationToken cancellationToken)
    {
        List<DiscoveredApplication> results = [];
        HashSet<string> seenIds = new(StringComparer.OrdinalIgnoreCase);

        HRESULT hr = PInvoke.SHAssocEnumHandlers(
            normalizedExtension,
            ASSOC_FILTER.ASSOC_FILTER_NONE,
            out IEnumAssocHandlers? enumerator);

        if (hr.Failed || enumerator is null)
        {
            return results;
        }

        try
        {
            IAssocHandler[] batch = new IAssocHandler[16];
            while (!cancellationToken.IsCancellationRequested)
            {
                uint fetched = 0;
                enumerator.Next((uint)batch.Length, batch, &fetched);
                if (fetched == 0)
                {
                    break;
                }

                for (int i = 0; i < fetched; i++)
                {
                    IAssocHandler handler = batch[i];
                    try
                    {
                        DiscoveredApplication? app = TryReadHandler(handler, includeIcons);
                        if (app is not null && seenIds.Add(app.Id))
                        {
                            results.Add(app);
                        }
                    }
                    finally
                    {
                        if (Marshal.IsComObject(handler))
                        {
                            Marshal.ReleaseComObject(handler);
                        }
                    }
                }
            }
        }
        finally
        {
            if (Marshal.IsComObject(enumerator))
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }

        return results
            .OrderByDescending(a => a.IsRecommended)
            .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    internal static bool MatchesTarget(DiscoveredApplication candidate, ApplicationTarget target)
    {
        if (!string.IsNullOrWhiteSpace(target.AppUserModelId) &&
            !string.IsNullOrWhiteSpace(candidate.AppUserModelId) &&
            string.Equals(target.AppUserModelId, candidate.AppUserModelId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(target.ProgId) &&
            !string.IsNullOrWhiteSpace(candidate.ProgId) &&
            string.Equals(target.ProgId, candidate.ProgId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(candidate.Id, target.ApplicationId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(target.ExecutablePath) &&
            !string.IsNullOrWhiteSpace(candidate.ExecutablePath) &&
            string.Equals(target.ExecutablePath, candidate.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    internal static unsafe IAssocHandler? FindLiveHandlerForTarget(string normalizedExtension, ApplicationTarget target)
    {
        HRESULT hr = PInvoke.SHAssocEnumHandlers(
            normalizedExtension,
            ASSOC_FILTER.ASSOC_FILTER_NONE,
            out IEnumAssocHandlers? enumerator);

        if (hr.Failed || enumerator is null)
        {
            return null;
        }

        try
        {
            IAssocHandler[] batch = new IAssocHandler[16];
            while (true)
            {
                uint fetched = 0;
                enumerator.Next((uint)batch.Length, batch, &fetched);
                if (fetched == 0)
                {
                    break;
                }

                for (int i = 0; i < fetched; i++)
                {
                    IAssocHandler handler = batch[i];
                    DiscoveredApplication? app = TryReadHandler(handler, includeIcons: false);
                    if (app is not null && MatchesTarget(app, target))
                    {
                        for (int j = i + 1; j < fetched; j++)
                        {
                            if (Marshal.IsComObject(batch[j]))
                            {
                                Marshal.ReleaseComObject(batch[j]);
                            }
                        }
                        return handler;
                    }

                    if (Marshal.IsComObject(handler))
                    {
                        Marshal.ReleaseComObject(handler);
                    }
                }
            }
        }
        finally
        {
            if (Marshal.IsComObject(enumerator))
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }

        return null;
    }

    internal static unsafe (string? ProgId, string? IconLocation, int IconIndex) TryFindHandlerIconInfo(
        string normalizedExtension,
        ApplicationTarget target)
    {
        IAssocHandler? handler = FindLiveHandlerForTarget(normalizedExtension, target);
        if (handler is null)
        {
            return (null, null, 0);
        }

        try
        {
            string? progId = null;
            if (handler is IObjectWithProgID withProgId)
            {
                progId = ReadCoTaskString(ptr => withProgId.GetProgID(ptr));
            }

            string? iconLocation = null;
            int iconIndex = 0;
            try
            {
                PWSTR iconPtr = default;
                handler.GetIconLocation(&iconPtr, out iconIndex);
                iconLocation = ConsumeCoTaskString(iconPtr);
            }
            catch
            {
            }

            return (progId, iconLocation, iconIndex);
        }
        finally
        {
            if (Marshal.IsComObject(handler))
            {
                Marshal.ReleaseComObject(handler);
            }
        }
    }

    private static unsafe DiscoveredApplication? TryReadHandler(IAssocHandler handler, bool includeIcons)
    {
        string? name = ReadCoTaskString(ptr => handler.GetName(ptr));
        string? uiName = ReadCoTaskString(ptr => handler.GetUIName(ptr));

        string? aumid = null;
        if (handler is IObjectWithAppUserModelID withAppId)
        {
            aumid = ReadCoTaskString(ptr => withAppId.GetAppID(ptr));
        }

        string? progId = null;
        if (handler is IObjectWithProgID withProgId)
        {
            progId = ReadCoTaskString(ptr => withProgId.GetProgID(ptr));
        }

        string? executablePath = (!string.IsNullOrWhiteSpace(name) &&
                                  Path.IsPathFullyQualified(name) &&
                                  name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            ? name
            : null;

        if (IsSelfHandler(name, uiName, progId, aumid, executablePath) ||
            IsSystemNoise(name, uiName, executablePath))
        {
            return null;
        }

        string? displayName = !string.IsNullOrWhiteSpace(uiName) ? uiName :
            !string.IsNullOrWhiteSpace(executablePath) ? Path.GetFileNameWithoutExtension(executablePath) : name;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        string stableId = !string.IsNullOrWhiteSpace(aumid) ? $"aumid:{aumid}" :
            !string.IsNullOrWhiteSpace(progId) ? $"progid:{progId}" :
            !string.IsNullOrWhiteSpace(executablePath) ? $"exe:{executablePath}" :
            $"name:{displayName}";

        string? iconLocation = null;
        int iconIndex = 0;
        byte[]? iconBytes = null;

        try
        {
            PWSTR iconPtr = default;
            handler.GetIconLocation(&iconPtr, out iconIndex);
            iconLocation = ConsumeCoTaskString(iconPtr);
            if (includeIcons)
            {
                iconBytes = ShellIconExtractor.TryExtractIconPng(iconLocation, iconIndex, executablePath, 48);
            }
        }
        catch
        {
            if (includeIcons && !string.IsNullOrWhiteSpace(executablePath))
            {
                iconBytes = ShellIconExtractor.TryExtractFileIconPng(executablePath, 48);
            }
        }

        bool isRecommended = handler.IsRecommended().Value == 0;

        return new DiscoveredApplication
        {
            Id = stableId,
            DisplayName = displayName,
            Kind = ApplicationTargetKind.RegisteredHandler,
            ExecutablePath = executablePath,
            ProgId = progId,
            AppUserModelId = aumid,
            IsRecommended = isRecommended,
            IconLocation = iconLocation,
            IconPngBytes = iconBytes
        };
    }

    internal static bool IsSelfHandler(
        string? name,
        string? uiName,
        string? progId,
        string? aumid,
        string? executablePath)
    {
        if (Associations.PerUserFileAssociationService.IsRuleOpenProgId(progId))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(aumid) &&
            (aumid.StartsWith("RuleOpen.", StringComparison.OrdinalIgnoreCase) ||
             aumid.StartsWith("PaulBrinkmann.RuleOpen_", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        foreach (string? candidate in new[] { name, progId, executablePath })
        {
            if (!string.IsNullOrWhiteSpace(candidate) &&
                (candidate.Contains("RuleOpen.Dispatcher.exe", StringComparison.OrdinalIgnoreCase) ||
                 candidate.Contains("RuleOpen.Desktop.exe", StringComparison.OrdinalIgnoreCase) ||
                 candidate.Contains("PaulBrinkmann.RuleOpen", StringComparison.OrdinalIgnoreCase) ||
                 Associations.PerUserFileAssociationService.IsRuleOpenProgId(candidate)))
            {
                return true;
            }
        }

        return string.Equals(uiName, "RuleOpen", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uiName, "RuleOpen File Dispatcher", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSystemNoise(string? name, string? uiName, string? executablePath)
    {
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            string fileName = Path.GetFileName(executablePath);
            if (NoisySystemExecutables.Contains(fileName))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            string trimmed = Path.GetFileName(name.Trim('"'));
            if (NoisySystemExecutables.Contains(trimmed))
            {
                return true;
            }
        }

        return string.Equals(uiName, "Pick an app", StringComparison.OrdinalIgnoreCase);
    }

    private unsafe delegate void PwstrGetter(PWSTR* ptr);

    private static unsafe string? ReadCoTaskString(PwstrGetter getter)
    {
        try
        {
            PWSTR ptr = default;
            getter(&ptr);
            return ConsumeCoTaskString(ptr);
        }
        catch
        {
            return null;
        }
    }

    private static unsafe string? ConsumeCoTaskString(PWSTR ptr)
    {
        if (ptr.Value is null)
        {
            return null;
        }

        try
        {
            return new string(ptr.Value);
        }
        finally
        {
            Marshal.FreeCoTaskMem((IntPtr)ptr.Value);
        }
    }
}
