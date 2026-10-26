using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ApiTester.App;

/// <summary>Colours a network row's status by class: 2xx signal (Mint), 3xx warm, 4xx/5xx/error red.</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = "Text.Muted";
        if (value is NetworkEntry e)
        {
            if (e.Error is not null) key = "Red";
            else key = (e.StatusCode ?? 0) switch
            {
                >= 200 and < 300 => "Signal",
                >= 300 and < 400 => "Warm",
                >= 400 => "Red",
                _ => "Text.Muted"
            };
        }
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
