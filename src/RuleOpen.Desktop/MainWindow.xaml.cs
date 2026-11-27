using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using RuleOpen.Core.Interfaces;
using RuleOpen.Core.Models;
using RuleOpen.Desktop.Services;
using RuleOpen.Desktop.ViewModels;
using RuleOpen.Desktop.Views;
using Windows.Graphics;
using WinRT.Interop;

namespace RuleOpen.Desktop;

public sealed partial class MainWindow : Window
{
    private const int GwlWndProc = -4;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MinWidthDips = 720;
    private const int MinHeightDips = 520;

    private readonly IConfigurationStore _configurationStore;
    private readonly IFileAssociationService _fileAssociationService;
    private readonly IDiagnosticLogger _logger;
    private readonly ThemeService _themeService;
    private readonly nint _hwnd;
    private WndProcDelegate? _wndProcDelegate;
    private nint _origWndProc;
    private AppStartupArguments _startupArgs = new();
    private bool _startupHandled;

    public MainWindow(
        IConfigurationStore configurationStore,
        IFileAssociationService fileAssociationService,
        IDiagnosticLogger logger,
        ThemeService themeService)
    {
        _configurationStore = configurationStore;
        _fileAssociationService = fileAssociationService;
        _logger = logger;
        _themeService = themeService;

        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        ConfigureWindowChrome();
        InstallMinSizeHook();

        RootGrid.Loaded += MainWindow_Loaded;
    }

    public nint Hwnd => _hwnd;

    public void InitializeStartup(AppStartupArguments startupArgs)
    {
        _startupArgs = startupArgs;

        RuleOpenConfig config = _configurationStore.Load();
        _logger.IsEnabled = config.Settings.EnableDiagnosticLogging;

        // Run association & icon-route reconciliation off the UI thread so first launch
        // activates immediately (preventing Store MSIX activation timeouts while generating icons).
        _ = Task.Run(() =>
        {
            try
            {
                RuleOpenConfig bgConfig = _configurationStore.Load();
                int regCountBefore = bgConfig.Settings.RegisteredExtensions.Count;
                int fbCountBefore = bgConfig.ExtensionFallbacks.Count;
                bool starterIfEmpty = bgConfig.Rules.Count == 0 && bgConfig.Settings.RegisteredExtensions.Count == 0;
                _fileAssociationService.EnsureRegistered(bgConfig, registerStarterExtensionsIfEmpty: starterIfEmpty);
                if (bgConfig.Settings.RegisteredExtensions.Count != regCountBefore ||
                    bgConfig.ExtensionFallbacks.Count != fbCountBefore)
                {
                    _configurationStore.Save(bgConfig);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Associations", $"Startup registration check skipped: {ex.Message}");
            }
        });

        ThemePreference effectiveTheme = startupArgs.ThemeOverride ?? config.Settings.Theme;
        _themeService.AttachWindow(this, effectiveTheme);

        if (startupArgs.Intent == StartupIntent.TestFile)
        {
            SelectNavTag("Test");
        }
        else if (!string.IsNullOrWhiteSpace(startupArgs.InitialPage))
        {
            string tag = startupArgs.InitialPage.ToLowerInvariant() switch
            {
                "test" => "Test",
                "settings" => "Settings",
                "about" => "About",
                _ => "Rules"
            };
            SelectNavTag(tag);
        }
        else
        {
            SelectNavTag("Rules");
        }
    }

    public void NavigateToRules() => SelectNavTag("Rules");

    public async Task NavigateToTestFileAsync(string? filePath = null)
    {
        SelectNavTag("Test");
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            RuleTestViewModel vm = App.Current.Services.GetRequiredService<RuleTestViewModel>();
            vm.SelectedFilePath = filePath;
            await vm.EvaluateAsync();
        }
    }

    public async Task OpenRuleEditorAsync(
        OpeningRule? existingRule = null,
        string? prefillFile = null,
        string? prefillFolder = null)
    {
        RuleEditorViewModel vm = App.Current.Services.GetRequiredService<RuleEditorViewModel>();
        if (existingRule is not null)
        {
            vm.InitializeEdit(existingRule);
        }
        else if (!string.IsNullOrWhiteSpace(prefillFile))
        {
            vm.InitializeForFile(prefillFile);
        }
        else if (!string.IsNullOrWhiteSpace(prefillFolder))
        {
            vm.InitializeForFolder(prefillFolder);
        }
        else
        {
            vm.InitializeNew();
        }

        RuleEditorDialog dialog = new(vm, _hwnd)
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            OpeningRule saved = vm.BuildRule();
            RulesViewModel rulesVm = App.Current.Services.GetRequiredService<RulesViewModel>();
            rulesVm.SaveNewOrEditedRule(saved, existingRule is not null);
        }
    }

    public async Task OpenOnboardingAsync(bool forceShow = false)
    {
        RuleOpenConfig config = _configurationStore.Load();
        if (!forceShow && config.Settings.HasCompletedOnboarding)
        {
            return;
        }

        OnboardingDialog dialog = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme
        };

        ContentDialogResult result = await dialog.ShowAsync();
        config.Settings.HasCompletedOnboarding = true;
        _configurationStore.Save(config);

        if (result == ContentDialogResult.Primary)
        {
            await OpenRuleEditorAsync();
        }
    }

    public async Task OpenRecoveryAsync(string filePath, string? reason, string? ruleId)
    {
        RecoveryViewModel vm = App.Current.Services.GetRequiredService<RecoveryViewModel>();
        await vm.InitializeAsync(filePath, reason, ruleId);

        RecoveryDialog dialog = new(vm, _hwnd)
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary)
        {
            await OpenRuleEditorAsync(prefillFile: filePath);
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_startupHandled)
        {
            return;
        }
        _startupHandled = true;

        // Re-sync title bar caption button colors now that ActualTheme is resolved.
        _themeService.RefreshActualTheme();

        switch (_startupArgs.Intent)
        {
            case StartupIntent.CreateRuleFromFile when !string.IsNullOrWhiteSpace(_startupArgs.TargetPath):
                await OpenRuleEditorAsync(prefillFile: _startupArgs.TargetPath);
                break;

            case StartupIntent.CreateRuleFromFolder when !string.IsNullOrWhiteSpace(_startupArgs.TargetPath):
                await OpenRuleEditorAsync(prefillFolder: _startupArgs.TargetPath);
                break;

            case StartupIntent.TestFile when !string.IsNullOrWhiteSpace(_startupArgs.TargetPath):
                await NavigateToTestFileAsync(_startupArgs.TargetPath);
                break;

            case StartupIntent.FallbackRecovery when !string.IsNullOrWhiteSpace(_startupArgs.TargetPath):
                await OpenRecoveryAsync(_startupArgs.TargetPath, _startupArgs.Reason, _startupArgs.RuleId);
                break;

            default:
                await OpenOnboardingAsync(forceShow: false);
                break;
        }
    }

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args)
    {
        _themeService.RefreshActualTheme();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            NavigateToTag(tag);
        }
    }

    private void SelectNavTag(string tag)
    {
        foreach (object? entry in NavView.MenuItems.Concat(NavView.FooterMenuItems))
        {
            if (entry is NavigationViewItem item && string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                NavView.SelectedItem = item;
                NavigateToTag(tag);
                return;
            }
        }
    }

    private void NavigateToTag(string tag)
    {
        Type targetType = tag switch
        {
            "Rules" => typeof(RulesPage),
            "Test" => typeof(RuleTestPage),
            "Settings" => typeof(SettingsPage),
            "About" => typeof(AboutPage),
            _ => typeof(RulesPage)
        };

        if (ContentFrame.CurrentSourcePageType != targetType)
        {
            ContentFrame.Navigate(targetType, null, new DrillInNavigationTransitionInfo());
        }
    }

    private void ConfigureWindowChrome()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "RuleOpen.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        double scale = GetDpiForWindow(_hwnd) / 96.0;
        int targetW = (int)Math.Round(980 * scale);
        int targetH = (int)Math.Round(680 * scale);
        AppWindow.Resize(new SizeInt32(targetW, targetH));

        DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        int x = display.WorkArea.X + Math.Max(0, (display.WorkArea.Width - targetW) / 2);
        int y = display.WorkArea.Y + Math.Max(0, (display.WorkArea.Height - targetH) / 2);
        AppWindow.Move(new PointInt32(x, y));
    }

    private void InstallMinSizeHook()
    {
        _wndProcDelegate = WindowProc;
        nint procPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _origWndProc = SetWindowLongPtr(_hwnd, GwlWndProc, procPtr);
    }

    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WmGetMinMaxInfo && lParam != 0)
        {
            double scale = GetDpiForWindow(hwnd) / 96.0;
            MinMaxInfo info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            info.ptMinTrackSize.X = (int)Math.Round(MinWidthDips * scale);
            info.ptMinTrackSize.Y = (int)Math.Round(MinHeightDips * scale);
            Marshal.StructureToPtr(info, lParam, false);
        }

        return CallWindowProc(_origWndProc, hwnd, msg, wParam, lParam);
    }

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public PointStruct ptReserved;
        public PointStruct ptMaxSize;
        public PointStruct ptMaxPosition;
        public PointStruct ptMinTrackSize;
        public PointStruct ptMaxTrackSize;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hwnd, uint msg, nint wParam, nint lParam);
}
