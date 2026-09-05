using Microsoft.UI.Xaml.Data;

namespace PiStation.App.Views;

public sealed class BooleanChoiceConverter : IValueConverter
{
    public string TrueValue { get; set; } = string.Empty;

    public string FalseValue { get; set; } = string.Empty;

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? TrueValue : FalseValue;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
