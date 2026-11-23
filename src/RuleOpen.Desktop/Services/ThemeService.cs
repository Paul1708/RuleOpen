using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using RuleOpen.Core.Models;
using Windows.UI;
using WinRT.Interop;

namespace RuleOpen.Desktop.Services;

/// <summary>
/// Synchronizes XAML <see cref="ElementTheme"/>, DWM dark title bar attribute, and caption button colors.
/// </summary>
public sealed class ThemeService
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private Window? _window;
    private ThemePreference _current = ThemePreference.System;

    public ThemePreference CurrentTheme => _current;

    public void AttachWindow(Window window, ThemePreference initialTheme)
    {
        _window = window;
        ApplyTheme(initialTheme);
    }

    public void ApplyTheme(ThemePreference preference)
    {
        _current = preference;
        if (_window?.Content is not FrameworkElement root)
        {
            return;
        }

        ElementTheme elementTheme = preference switch
        {
            ThemePreference.Light => ElementTheme.Light,
            ThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        root.RequestedTheme = elementTheme;

        bool isDark = elementTheme == ElementTheme.Dark ||
                      (elementTheme == ElementTheme.Default && root.ActualTheme == ElementTheme.Dark);

        UpdateWindowChrome(isDark);
    }

    public void RefreshActualTheme()
    {
        if (_current == ThemePreference.System && _window?.Content is FrameworkElement root)
        {
            UpdateWindowChrome(root.ActualTheme == ElementTheme.Dark);
        }
    }

    private void UpdateWindowChrome(bool isDark)
    {
        if (_window is null)
        {
            return;
        }

        nint hwnd = WindowNative.GetWindowHandle(_window);
        int darkFlag = isDark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkFlag, sizeof(int));

        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        AppWindowTitleBar titleBar = _window.AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        if (isDark)
        {
            titleBar.ButtonForegroundColor = Colors.White;
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(24, 255, 255, 255);
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(40, 255, 255, 255);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(140, 255, 255, 255);
        }
        else
        {
            titleBar.ButtonForegroundColor = Color.FromArgb(255, 24, 24, 24);
            titleBar.ButtonHoverForegroundColor = Colors.Black;
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(18, 0, 0, 0);
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(30, 0, 0, 0);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(140, 0, 0, 0);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);
}
