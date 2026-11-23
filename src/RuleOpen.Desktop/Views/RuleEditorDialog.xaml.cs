using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RuleOpen.Desktop.ViewModels;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace RuleOpen.Desktop.Views;

public sealed partial class RuleEditorDialog : ContentDialog
{
    private readonly nint _ownerHwnd;

    public RuleEditorViewModel ViewModel { get; }

    public RuleEditorDialog(RuleEditorViewModel viewModel, nint ownerHwnd)
    {
        ViewModel = viewModel;
        _ownerHwnd = ownerHwnd;
        InitializeComponent();
    }

    private void NameTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        ViewModel.MarkNameManuallyEdited();
    }

    private void QuickExtButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string ext })
        {
            ViewModel.AppendExtensionSuggestion(ext);
        }
    }

    private async void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        FolderPicker picker = new();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _ownerHwnd);

        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            ViewModel.FolderPath = folder.Path;
        }
    }

    private async void BrowseExeButton_Click(object sender, RoutedEventArgs e)
    {
        FileOpenPicker picker = new();
        picker.FileTypeFilter.Add(".exe");
        InitializeWithWindow.Initialize(picker, _ownerHwnd);

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await ViewModel.SetCustomExecutableAsync(file.Path);
        }
    }

    private void SelectAppChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ApplicationChoiceViewModel choice })
        {
            ViewModel.SelectDiscoveredApp(choice);
        }
    }
}
