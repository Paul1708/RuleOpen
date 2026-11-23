using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuleOpen.Desktop.ViewModels;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace RuleOpen.Desktop.Views;

public sealed partial class AppPickerDialog : ContentDialog
{
    private readonly nint _ownerHwnd;

    public AppPickerViewModel ViewModel { get; }

    public AppPickerDialog(AppPickerViewModel viewModel, nint ownerHwnd)
    {
        ViewModel = viewModel;
        _ownerHwnd = ownerHwnd;
        InitializeComponent();
    }

    private void SelectAppChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ApplicationChoiceViewModel choice })
        {
            ViewModel.SelectChoice(choice);
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
}
