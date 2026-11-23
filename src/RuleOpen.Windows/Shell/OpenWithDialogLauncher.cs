using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace RuleOpen.Windows.Shell;

/// <summary>
/// Opens the native Windows "How do you want to open this file?" dialog safely.
/// </summary>
public static class OpenWithDialogLauncher
{
    public static unsafe bool ShowOpenWithDialog(string filePath, nint ownerHwnd = 0)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        return StaThreadRunner.Run(() =>
        {
            fixed (char* pFile = filePath)
            {
                OPENASINFO info = new()
                {
                    pcszFile = pFile,
                    pcszClass = default,
                    oaifInFlags = OPEN_AS_INFO_FLAGS.OAIF_EXEC | OPEN_AS_INFO_FLAGS.OAIF_HIDE_REGISTRATION
                };

                HRESULT hr = PInvoke.SHOpenWithDialog(new HWND(ownerHwnd), in info);
                return hr.Succeeded;
            }
        });
    }
}
