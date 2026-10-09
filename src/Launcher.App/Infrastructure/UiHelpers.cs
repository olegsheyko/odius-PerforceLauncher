using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PerforceLauncher.App.Infrastructure;

public static class TimeText
{
    /// <summary>«just now», «5 min ago», «2 h ago», иначе дата.</summary>
    public static string Ago(DateTime time)
    {
        var d = DateTime.Now - time;
        if (d < TimeSpan.FromSeconds(45)) return "just now";
        if (d < TimeSpan.FromMinutes(60)) return $"{Math.Max(1, (int)d.TotalMinutes)} min ago";
        if (d < TimeSpan.FromHours(24) && time.Date == DateTime.Today) return $"at {time:HH:mm}";
        return $"on {time:dd.MM HH:mm}";
    }
}

public static class Palette
{
    public static SolidColorBrush Frozen(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }

    public static readonly SolidColorBrush Ok = Frozen(0xFF3DD68C);
    public static readonly SolidColorBrush OkTint = Frozen(0x1F3DD68C);
    public static readonly SolidColorBrush Warn = Frozen(0xFFF5B547);
    public static readonly SolidColorBrush WarnTint = Frozen(0x1FF5B547);
    public static readonly SolidColorBrush Bad = Frozen(0xFFF2555A);
    public static readonly SolidColorBrush BadTint = Frozen(0x1FF2555A);
    public static readonly SolidColorBrush Neutral = Frozen(0xFF8E8E96);
    public static readonly SolidColorBrush NeutralTint = Frozen(0x1A8E8E96);

    // Градиенты монограмм проектов: стабильный выбор по имени.
    private static readonly (Color A, Color B)[] AvatarPairs =
    {
        (Color.FromRgb(0x63, 0x66, 0xF1), Color.FromRgb(0x8B, 0x5C, 0xF6)),
        (Color.FromRgb(0x0E, 0xA5, 0xE9), Color.FromRgb(0x22, 0xD3, 0xEE)),
        (Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0x34, 0xD3, 0x99)),
        (Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromRgb(0xF9, 0x73, 0x16)),
        (Color.FromRgb(0xEC, 0x48, 0x99), Color.FromRgb(0xF4, 0x3F, 0x5E)),
        (Color.FromRgb(0x64, 0x74, 0x8B), Color.FromRgb(0x94, 0xA3, 0xB8)),
    };

    public static Brush Avatar(string seed)
    {
        // string.GetHashCode рандомизирован между запусками, поэтому считаем свой стабильный хеш.
        var h = 0;
        foreach (var ch in seed) h = unchecked(h * 31 + ch);
        var (a, b) = AvatarPairs[(int)((uint)h % AvatarPairs.Length)];
        var brush = new LinearGradientBrush(a, b, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }
}

/// <summary>true -> Collapsed, false -> Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Пустая строка или 0 -> Collapsed. ConverterParameter=invert меняет смысл на противоположный.</summary>
public sealed class HasValueToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var has = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            _ => true,
        };
        if (parameter as string == "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
