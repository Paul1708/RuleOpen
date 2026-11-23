using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuleOpen.Core.Models;
using RuleOpen.Desktop.ViewModels;

namespace RuleOpen.Desktop.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Load();
    }

    private async void ToggleModernContextMenu_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ToggleModernContextMenuAsync();
    }

    private void OpenDeveloperSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsViewModel.OpenDeveloperSettings();
    }

    private void RegisterExtension_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RegisterExtensionInput();
    }

    private void RegisterCommonExtensions_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RegisterCommonExtensions();
    }

    private void UnregisterExtension_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string ext })
        {
            ViewModel.UnregisterExtension(ext);
        }
    }

    private void OpenDefaultApps_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenDefaultAppsSettings();
    }

    private async void ChooseGlobalFallback_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.MainWindow is not { } window)
        {
            return;
        }

        AppPickerViewModel pickerVm = App.Current.Services.GetRequiredService<AppPickerViewModel>();
        ApplicationTarget? current = ViewModel.GetGlobalFallbackTarget();
        await pickerVm.InitializeForGlobalFallbackAsync(current);

        AppPickerDialog dialog = new(pickerVm, window.Hwnd)
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary && pickerVm.SelectedTarget is not null)
        {
            await ViewModel.SetGlobalFallbackTargetAsync(pickerVm.SelectedTarget);
        }
    }

    private void ClearGlobalFallback_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearGlobalFallback();
    }

    private async void ChangeExtensionFallback_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string ext } || App.Current.MainWindow is not { } window)
        {
            return;
        }

        AppPickerViewModel pickerVm = App.Current.Services.GetRequiredService<AppPickerViewModel>();
        ApplicationTarget? current = ViewModel.GetExtensionFallbackTarget(ext);
        await pickerVm.InitializeForExtensionAsync(ext, current);

        AppPickerDialog dialog = new(pickerVm, window.Hwnd)
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary && pickerVm.SelectedTarget is not null)
        {
            await ViewModel.SetExtensionFallbackAsync(ext, pickerVm.SelectedTarget);
        }
    }

    private void AutoDetectExtensionFallback_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string ext })
        {
            ViewModel.AutoDetectExtensionFallback(ext);
        }
    }

    private void ClearExtensionFallback_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string ext })
        {
            ViewModel.ClearExtensionFallback(ext);
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenLogFolder();
    }
}
