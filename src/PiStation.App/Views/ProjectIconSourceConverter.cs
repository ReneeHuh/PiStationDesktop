using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PiStation.App.Views;

public sealed class ProjectIconSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is byte[] content)
        {
            var bitmap = new BitmapImage { DecodePixelWidth = 44 };
            _ = LoadAsync(bitmap, content);
            return bitmap;
        }
        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Uri.TryCreate(path, UriKind.Absolute, out var uri) ? new BitmapImage(uri) : null;
    }

    private static async Task LoadAsync(BitmapImage bitmap, byte[] content)
    {
        try
        {
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(content);
                await writer.StoreAsync();
            }
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
