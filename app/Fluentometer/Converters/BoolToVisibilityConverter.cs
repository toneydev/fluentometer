using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Fluentometer.Converters;

/// <summary>
/// Converts a <see cref="bool"/> to <see cref="Visibility"/>.
/// <c>true</c> → <see cref="Visibility.Visible"/>; <c>false</c> → <see cref="Visibility.Collapsed"/>.
///
/// Register in App.xaml resources as:
/// <code>
/// &lt;converters:BoolToVisibilityConverter x:Key="BoolToVisibility" /&gt;
/// </code>
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isVisible = value is true;
        // A non-null "Invert" parameter inverts the logic.
        if (parameter is "Invert") isVisible = !isVisible;
        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        var isVisible = value is Visibility.Visible;
        if (parameter is "Invert") isVisible = !isVisible;
        return isVisible;
    }
}
