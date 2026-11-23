using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using RuleOpen.Core.Interfaces;
using RuleOpen.Core.Rules;
using RuleOpen.Core.Services;
using RuleOpen.Desktop.Services;
using RuleOpen.Desktop.ViewModels;
using RuleOpen.Windows;
using RuleOpen.Windows.Applications;
using RuleOpen.Windows.Associations;
using RuleOpen.Windows.Shell;

namespace RuleOpen.Desktop;

public sealed partial class App : Application
{
    public IServiceProvider Services { get; }
    public MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
        UnhandledException += OnUnhandledException;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            IDiagnosticLogger logger = Services.GetRequiredService<IDiagnosticLogger>();
            logger.IsEnabled = true;
            string stack = e.Exception?.StackTrace is { Length: > 0 } st
                ? $" | Stack: {st.Replace(Environment.NewLine, " <- ", StringComparison.Ordinal)}"
                : string.Empty;
            logger.LogError("App", $"Unhandled UI exception: {e.Message}{stack}", e.Exception);
        }
        catch
        {
        }
    }

    public static new App Current => (App)Application.Current;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] rawArgs = Environment.GetCommandLineArgs();
        AppStartupArguments startupArgs = AppStartupArguments.Parse(rawArgs.Length > 1 ? rawArgs[1..] : []);

        MainWindow = Services.GetRequiredService<MainWindow>();
        MainWindow.InitializeStartup(startupArgs);
        MainWindow.Activate();
    }

    private static ServiceProvider ConfigureServices()
    {
        ServiceCollection services = new();

        services.AddSingleton<IFileSystem, WindowsFileSystem>();
        services.AddSingleton<IDiagnosticLogger>(_ => new RollingFileLogger());
        services.AddSingleton<IConfigurationStore>(sp =>
            new JsonConfigurationStore(logger: sp.GetRequiredService<IDiagnosticLogger>()));
        services.AddSingleton<IRuleEngine, RuleEngine>();
        services.AddSingleton<IApplicationDiscoveryService, ShellApplicationDiscoveryService>();
        services.AddSingleton<IApplicationLauncher, ShellApplicationLauncher>();
        services.AddSingleton<IFileAssociationService, PerUserFileAssociationService>();
        services.AddSingleton<IContextMenuService, ContextMenuRegistrationService>();
        services.AddSingleton<DispatchCoordinator>();

        services.AddSingleton<ThemeService>();
        services.AddSingleton<IconCacheService>();

        services.AddSingleton<RulesViewModel>();
        services.AddTransient<RuleEditorViewModel>();
        services.AddTransient<AppPickerViewModel>();
        services.AddSingleton<RuleTestViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<RecoveryViewModel>();

        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
