using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace singC.Converters
{
    public class BoolToVisibilityConverter : IValueConverter
    {
        // 支持 Reverse 参数，ConverterParameter="Reverse" 时 true→Collapsed, false→Visible
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool boolValue = value is true;
            bool reverse = parameter is string p && p.Equals("Reverse", StringComparison.OrdinalIgnoreCase);
            if (reverse) boolValue = !boolValue;
            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}