using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using RuleOpen.Core.Models;

namespace RuleOpen.Dispatcher;

internal static class UninstallDialogHelper
{
    private const int TdfAllowDialogCancellation = 0x0008;
    private const int TdfUseCommandLinks = 0x0010;
    private const int TdfSizeToContent = 0x01000000;

    private const uint MbYesNo = 0x00000004;
    private const uint MbIconQuestion = 0x00000020;
    private const uint MbIconInformation = 0x00000040;
    private const uint MbDefButton1 = 0x00000000;
    private const uint MbDefButton2 = 0x00000100;
    private const uint MbTopMost = 0x00040000;
    private const int IdYes = 6;

    private static readonly nint TdWarningIcon = unchecked((nint)(ushort)0xFFFF);
    private static readonly nint TdInformationIcon = unchecked((nint)(ushort)0xFFFD);

    private const int KeepDataButtonId = 100;
    private const int DeleteDataButtonId = 101;
    private const int OpenDefaultAppsButtonId = 200;
    private const int DoneButtonId = 201;

    public static bool PromptDeleteUserData(string configDir, int ruleCount)
    {
        string rulesPhrase = ruleCount == 1 ? "1 saved opening rule" : $"{ruleCount} saved opening rules";
        string instruction = "Do you want to delete your RuleOpen rules and settings?";
        string content =
            $"RuleOpen found {rulesPhrase} and local configuration stored in:\n{configDir}\n\n" +
            "Keeping this data lets your existing rules and fallback settings work automatically if you reinstall RuleOpen later.";

        if (TryShowTaskDialog(
                title: "Uninstall RuleOpen",
                mainInstruction: instruction,
                content: content,
                iconResource: TdInformationIcon,
                useCommandLinks: true,
                buttons:
                [
                    (KeepDataButtonId, "Keep my rules and settings\nPreserve rules, fallback apps, and preferences in AppData."),
                    (DeleteDataButtonId, "Delete all user data\nPermanently remove saved rules, fallback mappings, icons, and logs.")
                ],
                defaultButtonId: KeepDataButtonId,
                out int pressedButton))
        {
            return pressedButton == DeleteDataButtonId;
        }

        int mbResult = MessageBoxW(
            0,
            $"{instruction}\n\n{content}\n\nSelect Yes to permanently delete all RuleOpen user data, or No to keep it.",
            "Uninstall RuleOpen",
            MbYesNo | MbIconQuestion | MbDefButton2 | MbTopMost);
        return mbResult == IdYes;
    }

    public static bool PromptRestoreDefaultApps(IReadOnlyList<FileAssociationStatus> activeDefaults)
    {
        if (activeDefaults.Count == 0)
        {
            return false;
        }

        StringBuilder listBuilder = new();
        foreach (FileAssociationStatus item in activeDefaults.Take(10))
        {
            string fallbackText = !string.IsNullOrWhiteSpace(item.CapturedFallbackDisplayName)
                ? $"Captured fallback: {item.CapturedFallbackDisplayName}"
                : "No prior default recorded";
            listBuilder.AppendLine(CultureInfo.InvariantCulture, $"• {item.Extension}   —   {fallbackText}");
        }

        if (activeDefaults.Count > 10)
        {
            listBuilder.AppendLine(CultureInfo.InvariantCulture, $"• (+{activeDefaults.Count - 10} more file types)");
        }

        string instruction = "Re-assign default apps for files previously using RuleOpen";
        string content =
            "RuleOpen's file-type registrations have been removed cleanly. Because Windows 11 protects user file associations, " +
            "Windows does not allow uninstallers to rewrite UserChoice defaults directly.\n\n" +
            $"Affected file types ({activeDefaults.Count}):\n{listBuilder}\n" +
            "Would you like to open Windows Default Apps now to re-select your preferred apps?";

        if (TryShowTaskDialog(
                title: "Uninstall RuleOpen",
                mainInstruction: instruction,
                content: content,
                iconResource: TdWarningIcon,
                useCommandLinks: false,
                buttons:
                [
                    (OpenDefaultAppsButtonId, "Open Windows Default Apps"),
                    (DoneButtonId, "Done")
                ],
                defaultButtonId: OpenDefaultAppsButtonId,
                out int pressedButton))
        {
            return pressedButton == OpenDefaultAppsButtonId;
        }

        int mbResult = MessageBoxW(
            0,
            $"{instruction}\n\n{content}",
            "Uninstall RuleOpen",
            MbYesNo | MbIconInformation | MbDefButton1 | MbTopMost);
        return mbResult == IdYes;
    }

    private static bool TryShowTaskDialog(
        string title,
        string mainInstruction,
        string content,
        nint iconResource,
        bool useCommandLinks,
        (int Id, string Text)[] buttons,
        int defaultButtonId,
        out int pressedButton)
    {
        pressedButton = defaultButtonId;
        List<nint> allocated = [];
        GCHandle buttonsHandle = default;

        try
        {
            TASKDIALOG_BUTTON[] nativeButtons = new TASKDIALOG_BUTTON[buttons.Length];
            for (int i = 0; i < buttons.Length; i++)
            {
                nint txtPtr = Marshal.StringToHGlobalUni(buttons[i].Text);
                allocated.Add(txtPtr);
                nativeButtons[i] = new TASKDIALOG_BUTTON
                {
                    nButtonID = buttons[i].Id,
                    pszButtonText = txtPtr
                };
            }

            buttonsHandle = GCHandle.Alloc(nativeButtons, GCHandleType.Pinned);
            nint titlePtr = Marshal.StringToHGlobalUni(title);
            nint instructionPtr = Marshal.StringToHGlobalUni(mainInstruction);
            nint contentPtr = Marshal.StringToHGlobalUni(content);
            allocated.Add(titlePtr);
            allocated.Add(instructionPtr);
            allocated.Add(contentPtr);

            TASKDIALOGCONFIG config = new()
            {
                cbSize = (uint)Marshal.SizeOf<TASKDIALOGCONFIG>(),
                hwndParent = 0,
                hInstance = 0,
                dwFlags = TdfAllowDialogCancellation | TdfSizeToContent | (useCommandLinks ? TdfUseCommandLinks : 0),
                dwCommonButtons = 0,
                pszWindowTitle = titlePtr,
                pszMainIcon = iconResource,
                pszMainInstruction = instructionPtr,
                pszContent = contentPtr,
                cButtons = (uint)nativeButtons.Length,
                pButtons = buttonsHandle.AddrOfPinnedObject(),
                nDefaultButton = defaultButtonId
            };

            int hr = TaskDialogIndirect(ref config, out int buttonId, 0, 0);
            if (hr == 0)
            {
                pressedButton = buttonId;
                return true;
            }
        }
        catch
        {
        }
        finally
        {
            if (buttonsHandle.IsAllocated)
            {
                buttonsHandle.Free();
            }

            foreach (nint ptr in allocated)
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TASKDIALOG_BUTTON
    {
        public int nButtonID;
        public nint pszButtonText;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TASKDIALOGCONFIG
    {
        public uint cbSize;
        public nint hwndParent;
        public nint hInstance;
        public int dwFlags;
        public int dwCommonButtons;
        public nint pszWindowTitle;
        public nint pszMainIcon;
        public nint pszMainInstruction;
        public nint pszContent;
        public uint cButtons;
        public nint pButtons;
        public int nDefaultButton;
        public uint cRadioButtons;
        public nint pRadioButtons;
        public int nDefaultRadioButton;
        public nint pszVerificationText;
        public nint pszExpandedInformation;
        public nint pszExpandedControlText;
        public nint pszCollapsedControlText;
        public nint pszFooterIcon;
        public nint pszFooter;
        public nint pfCallback;
        public nint lpCallbackData;
        public uint cxWidth;
    }

    [DllImport("comctl32.dll")]
    private static extern int TaskDialogIndirect(
        ref TASKDIALOGCONFIG pTaskConfig,
        out int pnButton,
        nint pnRadioButton,
        nint pfVerificationFlagChecked);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);
}
