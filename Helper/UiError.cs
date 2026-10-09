using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace singC.Helpers;

internal static class UiError
{
    public static async Task ShowAsync(XamlRoot root, string action, Exception error)
    {
        Debug.WriteLine(action + ": " + error);
        try { await new ContentDialog { XamlRoot = root, Title = action + "失败", Content = error.Message, CloseButtonText = "关闭" }.ShowAsync(); }
        catch (Exception dialogError) { Debug.WriteLine(dialogError); }
    }
}
