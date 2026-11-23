using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuleOpen.Desktop.ViewModels;

namespace RuleOpen.Desktop.Views;

public sealed partial class RecoveryDialog : ContentDialog
{
    private readonly nint _ownerHwnd;

    public RecoveryViewModel ViewModel { get; }

    public RecoveryDialog(RecoveryViewModel viewModel, nint ownerHwnd)
    {
        ViewModel = viewModel;
        _ownerHwnd = ownerHwnd;
        InitializeComponent();
    }

    private async void OpenWithAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ApplicationChoiceViewModel choice })
        {
            bool remember = SaveRuleCheckBox.IsChecked == true;
            if (await ViewModel.LaunchWithAsync(choice, remember))
            {
                Hide();
            }
        }
    }

    private void WindowsOpenWithButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ShowWindowsOpenWith(_ownerHwnd))
        {
            Hide();
        }
    }
}
