using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace PiStation.App.Views;

internal static class ThemeResourceLookup
{
    public static T Get<T>(FrameworkElement owner, string key)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (TryGet(owner, key, out var value) && value is T typed)
        {
            return typed;
        }

        throw new KeyNotFoundException($"Theme resource '{key}' was not found.");
    }

    public static T Get<T>(ElementTheme theme, string key)
    {
        if (TryGet(theme, key, out var value) && value is T typed)
        {
            return typed;
        }

        throw new KeyNotFoundException($"Theme resource '{key}' was not found.");
    }

    public static bool TryGet(FrameworkElement owner, string key, out object? value)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (TryGet(owner.ActualTheme, key, out value))
        {
            return true;
        }

        return Application.Current.Resources.TryGetValue(key, out value);
    }

    public static bool TryGet(ElementTheme requestedTheme, string key, out object? value)
    {
        var resources = Application.Current.Resources;
        var theme = new AccessibilitySettings().HighContrast
            ? "HighContrast"
            : requestedTheme == ElementTheme.Dark ? "Dark" : "Light";
        foreach (var dictionary in resources.MergedDictionaries.Reverse())
        {
            if (dictionary.ThemeDictionaries.TryGetValue(theme, out var themeObject) &&
                themeObject is ResourceDictionary themeDictionary &&
                themeDictionary.TryGetValue(key, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }
}
