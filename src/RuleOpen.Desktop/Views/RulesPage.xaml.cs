using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuleOpen.Desktop.ViewModels;

namespace RuleOpen.Desktop.Views;

public sealed partial class RulesPage : Page
{
    public RulesViewModel ViewModel { get; }

    public RulesPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<RulesViewModel>();
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Load();
    }

    private async void NewRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.MainWindow is { } window)
        {
            await window.OpenRuleEditorAsync();
        }
    }

    private async void ShowOnboardingButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.MainWindow is { } window)
        {
            await window.OpenOnboardingAsync(forceShow: true);
        }
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SearchQuery = string.Empty;
    }

    private void DefaultAppsInfoBar_CloseButtonClick(InfoBar sender, object args)
    {
        ViewModel.DismissDefaultAppsPrompt();
    }

    private void OpenDefaultAppsButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenDefaultAppsSettings();
    }

    private async void RulesListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RuleItemViewModel item && App.Current.MainWindow is { } window)
        {
            await window.OpenRuleEditorAsync(existingRule: item.Rule);
        }
    }

    private async void EditMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RuleItemViewModel item } && App.Current.MainWindow is { } window)
        {
            await window.OpenRuleEditorAsync(existingRule: item.Rule);
        }
    }

    private void DuplicateMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RuleItemViewModel item })
        {
            ViewModel.DuplicateRule(item);
        }
    }

    private async void TestRuleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RuleItemViewModel item } && App.Current.MainWindow is { } window)
        {
            string ext = item.Extensions.Count > 0 ? item.Extensions[0] : ".pdf";
            string samplePath = Path.Combine(item.FolderPath, "example" + ext);
            await window.NavigateToTestFileAsync(samplePath);
        }
    }

    private void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RuleItemViewModel item })
        {
            ViewModel.DeleteRule(item);
        }
    }
}
