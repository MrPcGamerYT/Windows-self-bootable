using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using UsbFreeInstaller.Core.Models;

namespace UsbFreeInstaller.App.Infrastructure;

public sealed class BooleanToVisibilityConverterEx : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is true;
        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasContent = value switch
        {
            null => false,
            var v when ReferenceEquals(v, DependencyProperty.UnsetValue) => false,
            string str => !string.IsNullOrWhiteSpace(str),
            _ => true
        };

        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            hasContent = !hasContent;
        }

        return hasContent ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class ActiveStepOrTabBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush ActiveBrush = CreateFrozenBrush(0x02, 0x84, 0xC7);   // Accent Sky Blue
    private static readonly SolidColorBrush InactiveBrush = CreateFrozenBrush(0x16, 0x20, 0x33); // Refined Deep Slate

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ActiveBrush : InactiveBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public sealed class ActiveBorderBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush ActiveBorder = CreateFrozenBrush(0x38, 0xBD, 0xF8);
    private static readonly SolidColorBrush InactiveBorder = CreateFrozenBrush(0x28, 0x35, 0x48);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ActiveBorder : InactiveBorder;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public sealed class ExecutionModePillBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush SafeDryRunBrush = CreateFrozenArgbBrush(0x33, 0x10, 0xB9, 0x81);
    private static readonly SolidColorBrush LiveModeBrush = CreateFrozenArgbBrush(0x3B, 0xEF, 0x44, 0x44);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? SafeDryRunBrush : LiveModeBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static SolidColorBrush CreateFrozenArgbBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}

public sealed class ExecutionModeTextBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush SafeTextBrush = CreateFrozenBrush(0x34, 0xD3, 0x99);
    private static readonly SolidColorBrush LiveTextBrush = CreateFrozenBrush(0xFC, 0xA5, 0xA5);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? SafeTextBrush : LiveTextBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public sealed class SeverityToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush PassBrush = CreateFrozenBrush(0x10, 0xB9, 0x81);    // Emerald
    private static readonly SolidColorBrush InfoBrush = CreateFrozenBrush(0x38, 0xBD, 0xF8);    // Sky
    private static readonly SolidColorBrush WarnBrush = CreateFrozenBrush(0xF5, 0x9E, 0x0B);    // Amber
    private static readonly SolidColorBrush BlockBrush = CreateFrozenBrush(0xEF, 0x44, 0x44);   // Red

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            CheckSeverity.Pass => PassBrush,
            CheckSeverity.Info => InfoBrush,
            CheckSeverity.Warning => WarnBrush,
            CheckSeverity.Blocker => BlockBrush,
            _ => InfoBrush
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public sealed class SeverityToSurfaceBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush PassSurface = CreateFrozenArgbBrush(0x14, 0x10, 0xB9, 0x81);
    private static readonly SolidColorBrush InfoSurface = CreateFrozenArgbBrush(0x14, 0x38, 0xBD, 0xF8);
    private static readonly SolidColorBrush WarnSurface = CreateFrozenArgbBrush(0x1D, 0xF5, 0x9E, 0x0B);
    private static readonly SolidColorBrush BlockSurface = CreateFrozenArgbBrush(0x22, 0xEF, 0x44, 0x44);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            CheckSeverity.Pass => PassSurface,
            CheckSeverity.Info => InfoSurface,
            CheckSeverity.Warning => WarnSurface,
            CheckSeverity.Blocker => BlockSurface,
            _ => InfoSurface
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static SolidColorBrush CreateFrozenArgbBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}

public sealed class SeverityToBadgeTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            CheckSeverity.Pass => "PASS",
            CheckSeverity.Info => "INFO",
            CheckSeverity.Warning => "WARNING",
            CheckSeverity.Blocker => "BLOCKER",
            _ => "INFO"
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class SetupActionToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush DeleteTargetBrush = CreateFrozenBrush(0xEA, 0x58, 0x0C);   // Vibrant Orange
    private static readonly SolidColorBrush PreserveSourceBrush = CreateFrozenBrush(0x7C, 0x3A, 0xED); // Violet
    private static readonly SolidColorBrush PreserveSystemBrush = CreateFrozenBrush(0x0D, 0x94, 0x88); // Teal
    private static readonly SolidColorBrush PreserveOtherBrush = CreateFrozenBrush(0x47, 0x55, 0x69);  // Slate

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            SetupPartitionAction.DeleteOrFormatTarget => DeleteTargetBrush,
            SetupPartitionAction.PreserveInstallerSource => PreserveSourceBrush,
            SetupPartitionAction.PreserveSystem => PreserveSystemBrush,
            _ => PreserveOtherBrush
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
