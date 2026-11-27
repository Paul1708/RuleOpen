using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RuleOpen.Desktop.Views;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        var version = typeof(AboutPage).Assembly.GetName().Version;
        if (version is not null)
        {
            VersionTextBlock.Text = $"Version {version.Major}.{version.Minor}.{version.Build}  •  Windows 11 Native Utility";
        }
    }

    private void StoreCard_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://apps.microsoft.com/detail/9NPTZJ83X1W9",
            UseShellExecute = true
        });
    }

    private void GitHubCard_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/Paul1708/RuleOpen",
            UseShellExecute = true
        });
    }

    private async void ShowTourCard_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.MainWindow is { } window)
        {
            await window.OpenOnboardingAsync(forceShow: true);
        }
    }
}
