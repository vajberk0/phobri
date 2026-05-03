using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace Phobri.Desktop.Infrastructure;

/// <summary>
/// Converts a PNG byte array to an Avalonia Bitmap for display in Image controls.
/// </summary>
public sealed class ByteArrayToBitmapConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is byte[] bytes && bytes.Length > 0)
        {
            try
            {
                using var stream = new System.IO.MemoryStream(bytes);
                return new Bitmap(stream);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
