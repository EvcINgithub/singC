using Microsoft.UI.Xaml.Data;
using System;

namespace singC.Converters
{
    public class BoolToStringConverter : IValueConverter
    {
        // 根据 bool 值和参数（用 '|' 分隔的两个字符串）返回对应文本
        // 例如 ConverterParameter="简易模式|高级模式"
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool boolValue = value is true;
            string? options = parameter as string;
            if (string.IsNullOrEmpty(options)) return boolValue.ToString();

            string[] parts = options.Split('|');
            if (parts.Length == 2)
            {
                return boolValue ? parts[1] : parts[0];  // true 对应高级模式（第二个）
            }
            return boolValue.ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
