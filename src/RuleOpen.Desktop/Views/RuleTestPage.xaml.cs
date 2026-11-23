using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RuleOpen.Desktop.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace RuleOpen.Desktop.Views;

public sealed partial class RuleTestPage : Page
{
    public RuleTestViewModel ViewModel { get; }

    public RuleTestPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<RuleTestViewModel>();
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(ViewModel.SelectedFilePath))
            {
                await ViewModel.EvaluateAsync();
            }
        };
    }

    private async void BrowseFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.MainWindow is not { } window)
        {
            return;
        }

        FileOpenPicker picker = new();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, window.Hwnd);

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            ViewModel.SelectedFilePath = file.Path;
            await ViewModel.EvaluateAsync();
        }
    }

    private async void EvaluateButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.EvaluateAsync();
    }

    private async void FilePathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel.CanEvaluate)
        {
            await ViewModel.EvaluateAsync();
        }
    }

    private async void TestOpenNowButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.TestOpenNowAsync();
    }

    private async void CreateRuleFromTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.MainWindow is { } window)
        {
            await window.OpenRuleEditorAsync(prefillFile: ViewModel.SelectedFilePath);
        }
    }

    private void OpenDefaultAppsButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenDefaultApps();
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
        if (items.Count > 0 && items[0] is StorageFile file)
        {
            ViewModel.SelectedFilePath = file.Path;
            await ViewModel.EvaluateAsync();
        }
    }
}
