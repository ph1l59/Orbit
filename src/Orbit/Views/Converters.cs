using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Orbit.Views;

/// <summary>Turns an image URL into a (cached) bitmap; returns null for empty values.</summary>
public sealed class UrlToImageConverter : IValueConverter
{
    private static readonly Dictionary<string, BitmapImage> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url)) return null;
        if (Cache.TryGetValue(url, out var cached)) return cached;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(url);
            image.DecodePixelWidth = 96;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            Cache[url] = image;
            return image;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Visible when the string is not empty.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
