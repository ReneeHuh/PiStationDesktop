using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace PiStation.App.Views;

public sealed class ObjectPresenceToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
