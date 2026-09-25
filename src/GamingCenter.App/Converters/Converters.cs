using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GamingCenter.Application.Common;
using GamingCenter.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace GamingCenter.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool v = value is true;
        if (Invert) v = !v;
        return v ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is Visibility.Visible ^ Invert;
}

/// <summary>Visible when the value is non-null and not an empty string/zero-count collection.</summary>
public sealed class HasValueToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool has = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            decimal d => d != 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        if (Invert) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True when value.ToString() equals the parameter; used for RadioButtons bound to enums/strings.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null) return Binding.DoNothing;
        var t = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (t.IsEnum) return Enum.Parse(t, parameter.ToString()!);
        if (t == typeof(int)) return int.Parse(parameter.ToString()!, CultureInfo.InvariantCulture);
        return parameter;
    }
}

public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool eq = string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
        return eq ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Looks up a theme brush by key, e.g. "Status.Available". Parameter is an optional key prefix.</summary>
public sealed class ResourceBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (parameter as string ?? "") + value;
        return System.Windows.Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class MoneyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        decimal d when parameter as string == "number" => Money.Number(d),
        decimal d => Money.Format(d),
        _ => "",
    };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Money.TryParse(value as string, out var d) ? d : Binding.DoNothing;
}

/// <summary>0..1 → GridLength star values; used for bars (parameter "rest" returns the remainder).</summary>
public sealed class FractionToStarConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double f = value switch { double d => d, decimal m => (double)m, int i => i, _ => 0 };
        f = Math.Clamp(double.IsNaN(f) ? 0 : f, 0, 1);
        if (parameter as string == "rest") f = 1 - f;
        return new GridLength(Math.Max(f, 0.0001), GridUnitType.Star);
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Loads station/product images from the local image store as small cached thumbnails,
/// so large files are never decoded at full size.
/// </summary>
public sealed class ImagePathConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new();

    public int DecodeWidth { get; set; } = 320;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string rel || string.IsNullOrWhiteSpace(rel)) return null;
        var store = App.Services?.GetService<IImageStore>();
        var path = store?.Resolve(rel) ?? rel;
        if (!File.Exists(path)) return null;

        var key = $"{path}|{DecodeWidth}|{File.GetLastWriteTimeUtc(path).Ticks}";
        return Cache.GetOrAdd(key, _ =>
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // do not lock the file
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.DecodePixelWidth = DecodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        });
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
