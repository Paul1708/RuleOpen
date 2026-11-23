using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using RuleOpen.Core.Interfaces;
using RuleOpen.Core.Models;
using RuleOpen.Core.Rules;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace RuleOpen.Windows.Applications;

/// <summary>
/// Launches target applications via Windows Shell association handlers (<c>IAssocHandler</c>,
/// <c>IApplicationActivationManager</c>) or validated process invocation, with strict protection
/// against recursive RuleOpen self-dispatch.
/// </summary>
public sealed class ShellApplicationLauncher : IApplicationLauncher
{
    public const string DispatchDepthEnvVar = "RULEOPEN_DISPATCH_DEPTH";
    public const string DispatchChainEnvVar = "RULEOPEN_DISPATCH_CHAIN";
    private const int MaxDispatchDepth = 2;

    public bool IsSelfTarget(ApplicationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (ShellApplicationDiscoveryService.IsSelfHandler(
                target.ApplicationId,
                target.DisplayName,
                target.ProgId,
                target.AppUserModelId,
                target.ExecutablePath))
        {
            return true;
        }

        string? currentExe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(currentExe) &&
            !string.IsNullOrWhiteSpace(target.ExecutablePath) &&
            string.Equals(
                Path.GetFullPath(currentExe),
                Path.GetFullPath(target.ExecutablePath),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public Task<LaunchResult> LaunchAsync(
        ApplicationTarget target,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() => LaunchCore(target, filePath), cancellationToken);
    }

    private LaunchResult LaunchCore(ApplicationTarget target, string filePath)
    {
        if (IsSelfTarget(target))
        {
            return LaunchResult.Fail(
                LaunchFailureReason.RecursiveDispatchBlocked,
                "The target application resolves to RuleOpen itself, which would cause an infinite launch loop.");
        }

        if (!File.Exists(filePath))
        {
            return LaunchResult.Fail(
                LaunchFailureReason.FileNotFound,
                $"The file \"{filePath}\" no longer exists.");
        }

        if (IsRecursionChainExceeded(filePath, out int currentDepth, out string updatedChain))
        {
            return LaunchResult.Fail(
                LaunchFailureReason.RecursiveDispatchBlocked,
                "RuleOpen detected a repeated dispatch for this file and stopped to prevent a launch loop.",
                $"Depth={currentDepth}");
        }

        string prevDepth = Environment.GetEnvironmentVariable(DispatchDepthEnvVar) ?? string.Empty;
        string prevChain = Environment.GetEnvironmentVariable(DispatchChainEnvVar) ?? string.Empty;

        try
        {
            Environment.SetEnvironmentVariable(DispatchDepthEnvVar, (currentDepth + 1).ToString(CultureInfo.InvariantCulture));
            Environment.SetEnvironmentVariable(DispatchChainEnvVar, updatedChain);

            if (target.Kind == ApplicationTargetKind.CustomExecutable)
            {
                return LaunchCustomExecutable(target, filePath);
            }

            return LaunchRegisteredHandler(target, filePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                DispatchDepthEnvVar,
                string.IsNullOrEmpty(prevDepth) ? null : prevDepth);
            Environment.SetEnvironmentVariable(
                DispatchChainEnvVar,
                string.IsNullOrEmpty(prevChain) ? null : prevChain);
        }
    }

    private static bool IsRecursionChainExceeded(string filePath, out int currentDepth, out string updatedChain)
    {
        string? depthRaw = Environment.GetEnvironmentVariable(DispatchDepthEnvVar);
        currentDepth = int.TryParse(depthRaw, out int parsed) ? parsed : 0;

        string normalized = Path.GetFullPath(filePath);
        string? existingChain = Environment.GetEnvironmentVariable(DispatchChainEnvVar);
        if (!string.IsNullOrWhiteSpace(existingChain))
        {
            string[] items = existingChain.Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (items.Any(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase)) ||
                currentDepth >= MaxDispatchDepth)
            {
                updatedChain = existingChain;
                return true;
            }

            updatedChain = existingChain + "|" + normalized;
            return false;
        }

        updatedChain = normalized;
        return currentDepth >= MaxDispatchDepth;
    }

    private static LaunchResult LaunchCustomExecutable(ApplicationTarget target, string filePath)
    {
        string? exe = target.ExecutablePath ?? target.ApplicationId;
        if (string.IsNullOrWhiteSpace(exe) || !Path.IsPathFullyQualified(exe))
        {
            return LaunchResult.Fail(
                LaunchFailureReason.InvalidArguments,
                "The configured custom executable path is invalid.");
        }

        string resolvedExe = Path.GetFullPath(exe);
        if (!File.Exists(resolvedExe))
        {
            return LaunchResult.Fail(
                LaunchFailureReason.ApplicationNotFound,
                $"\"{target.DisplayName}\" could not be found at \"{resolvedExe}\".");
        }

        if (ShellApplicationDiscoveryService.IsSelfHandler(resolvedExe, target.DisplayName, null, null, resolvedExe))
        {
            return LaunchResult.Fail(
                LaunchFailureReason.RecursiveDispatchBlocked,
                "RuleOpen cannot launch itself as a custom executable.");
        }

        return StartProcessSafely(resolvedExe, filePath, target.DisplayName);
    }

    private static LaunchResult LaunchRegisteredHandler(ApplicationTarget target, string filePath)
    {
        string rawExt = Path.GetExtension(filePath);
        if (ExtensionNormalizer.TryNormalize(rawExt, out string normExt))
        {
            LaunchResult? handlerAttempt = StaThreadRunner.Run(() => TryInvokeShellAssocHandler(normExt, target, filePath));
            if (handlerAttempt is not null)
            {
                return handlerAttempt;
            }
        }

        if (!string.IsNullOrWhiteSpace(target.AppUserModelId))
        {
            LaunchResult? aumidAttempt = StaThreadRunner.Run(() => TryActivatePackagedApp(target.AppUserModelId!, filePath, target.DisplayName));
            if (aumidAttempt is not null && aumidAttempt.Success)
            {
                return aumidAttempt;
            }
        }

        if (!string.IsNullOrWhiteSpace(target.ExecutablePath) && File.Exists(target.ExecutablePath))
        {
            return StartProcessSafely(target.ExecutablePath, filePath, target.DisplayName);
        }

        return LaunchResult.Fail(
            LaunchFailureReason.ApplicationNotFound,
            $"\"{target.DisplayName}\" is no longer available to open {rawExt} files.");
    }

    private static unsafe LaunchResult? TryInvokeShellAssocHandler(string normalizedExtension, ApplicationTarget target, string filePath)
    {
        IAssocHandler? handler = ShellApplicationDiscoveryService.FindLiveHandlerForTarget(normalizedExtension, target);
        if (handler is null)
        {
            return null;
        }

        IShellItem? shellItem = null;
        IDataObject? dataObject = null;
        IAssocHandlerInvoker? invoker = null;

        try
        {
            HRESULT hr = PInvoke.SHCreateItemFromParsingName(filePath, pbc: null, out shellItem);
            if (hr.Failed || shellItem is null)
            {
                return LaunchResult.Fail(
                    LaunchFailureReason.HandlerUnavailable,
                    $"Could not bind shell item for \"{filePath}\".",
                    $"HRESULT=0x{hr.Value:X8}");
            }

            Guid bhid = PInvoke.BHID_DataObject;
            Guid iidDataObj = typeof(IDataObject).GUID;
            shellItem.BindToHandler(null!, &bhid, &iidDataObj, out void* pDataObj);
            if (pDataObj is null)
            {
                return LaunchResult.Fail(
                    LaunchFailureReason.HandlerUnavailable,
                    "Could not create shell data object for handler invocation.");
            }

            try
            {
                dataObject = (IDataObject)Marshal.GetObjectForIUnknown((IntPtr)pDataObj);
            }
            finally
            {
                Marshal.Release((IntPtr)pDataObj);
            }

            try
            {
                handler.CreateInvoker(dataObject, out invoker);
                if (invoker is not null)
                {
                    invoker.SupportsSelection();
                    invoker.Invoke();
                    return LaunchResult.Ok("Launched via IAssocHandlerInvoker.");
                }
            }
            catch (COMException)
            {
                // Some handlers only implement Invoke directly rather than CreateInvoker.
            }

            handler.Invoke(dataObject);
            return LaunchResult.Ok("Launched via IAssocHandler.Invoke.");
        }
        catch (COMException ex)
        {
            return LaunchResult.Fail(
                LaunchFailureReason.ProcessLaunchFailed,
                $"Windows failed to launch \"{target.DisplayName}\".",
                $"COMException 0x{ex.HResult:X8}: {ex.Message}");
        }
        finally
        {
            if (invoker is not null && Marshal.IsComObject(invoker))
            {
                Marshal.ReleaseComObject(invoker);
            }

            if (dataObject is not null && Marshal.IsComObject(dataObject))
            {
                Marshal.ReleaseComObject(dataObject);
            }

            if (shellItem is not null && Marshal.IsComObject(shellItem))
            {
                Marshal.ReleaseComObject(shellItem);
            }

            if (Marshal.IsComObject(handler))
            {
                Marshal.ReleaseComObject(handler);
            }
        }
    }

    private static unsafe LaunchResult? TryActivatePackagedApp(string aumid, string filePath, string displayName)
    {
        IShellItem? shellItem = null;
        IShellItemArray? itemArray = null;
        IApplicationActivationManager? manager = null;

        try
        {
            HRESULT hr = PInvoke.SHCreateItemFromParsingName(filePath, pbc: null, out shellItem);
            if (hr.Failed || shellItem is null)
            {
                return null;
            }

            HRESULT hrArray = PInvoke.SHCreateShellItemArrayFromShellItem(shellItem, out itemArray);
            if (hrArray.Failed || itemArray is null)
            {
                return null;
            }

            manager = (IApplicationActivationManager)new ApplicationActivationManager();
            manager.ActivateForFile(aumid, itemArray, "open", out _);
            return LaunchResult.Ok("Launched via IApplicationActivationManager.ActivateForFile.");
        }
        catch (COMException ex)
        {
            return LaunchResult.Fail(
                LaunchFailureReason.ProcessLaunchFailed,
                $"Could not activate packaged application \"{displayName}\".",
                $"COMException 0x{ex.HResult:X8}: {ex.Message}");
        }
        finally
        {
            if (manager is not null && Marshal.IsComObject(manager))
            {
                Marshal.ReleaseComObject(manager);
            }

            if (itemArray is not null && Marshal.IsComObject(itemArray))
            {
                Marshal.ReleaseComObject(itemArray);
            }

            if (shellItem is not null && Marshal.IsComObject(shellItem))
            {
                Marshal.ReleaseComObject(shellItem);
            }
        }
    }

    private static LaunchResult StartProcessSafely(string executablePath, string filePath, string displayName)
    {
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = executablePath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(filePath) ?? Environment.CurrentDirectory
            };
            psi.ArgumentList.Add(filePath);

            Process.Start(psi);
            return LaunchResult.Ok($"Launched process \"{Path.GetFileName(executablePath)}\".");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return LaunchResult.Fail(
                LaunchFailureReason.ApplicationNotFound,
                $"\"{displayName}\" could not be found at \"{executablePath}\".",
                ex.Message);
        }
        catch (Exception ex)
        {
            return LaunchResult.Fail(
                LaunchFailureReason.ProcessLaunchFailed,
                $"Failed to start \"{displayName}\": {ex.Message}",
                ex.ToString());
        }
    }
}
