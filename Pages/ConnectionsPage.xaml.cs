using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using singC.Models;
using Microsoft.UI.Dispatching;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace singC.Pages;

/// <summary>
/// An empty page that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class ConnectionsPage : Page
{
    public ConnectionViewModel ViewModel => ConnectionViewModel.Instance;

    public ConnectionsPage()
    {
        this.InitializeComponent();
    }

    private ConnectionInfo? SelectedConnection => ConnectionListView.SelectedItem as ConnectionInfo;

    private void ConnectionListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionActions();
    }

    private void ConnectionListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        ConnectionListView.SelectedItem = e.ClickedItem;
        UpdateSelectionActions();
    }

    private void UpdateSelectionActions()
    {
        bool hasSelection = SelectedConnection != null;
        CopyConnectionButton.IsEnabled = hasSelection;
        ConnectionDetailsButton.IsEnabled = hasSelection;
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SearchText = string.Empty;
        SearchBox.Text = string.Empty;
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshNow();
    }

    private void CopyConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection == null) return;

        var package = new DataPackage();
        package.SetText(SelectedConnection.ToClipboardText());
        Clipboard.SetContent(package);
    }

    private async void ConnectionDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        var connection = SelectedConnection;
        if (connection == null) return;

        var detailsBox = new TextBox
        {
            Text = connection.ToClipboardText(),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            IsSpellCheckEnabled = false,
            MinWidth = 420,
            MaxHeight = 360
        };

        var dialog = new ContentDialog
        {
            Title = "连接详情",
            Content = detailsBox,
            PrimaryButtonText = "复制",
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var package = new DataPackage();
            package.SetText(detailsBox.Text);
            Clipboard.SetContent(package);
        }
    }
}
