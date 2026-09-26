using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using singC.Models;
using System;

namespace singC.Converters;

public sealed class NetworkTestStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string key = value switch
        {
            NetworkTestStatus.Success => "SystemFillColorSuccessBrush",
            NetworkTestStatus.Warning => "SystemFillColorCautionBrush",
            NetworkTestStatus.Failed => "SystemFillColorCriticalBrush",
            _ => "TextFillColorSecondaryBrush"
        };
        return Application.Current.Resources[key];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
