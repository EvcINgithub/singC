using Microsoft.UI.Xaml.Controls;

namespace singC.Pages;

public sealed partial class TrafficStatisticsPage : Page
{
    public TrafficStatisticsPage()
    {
        InitializeComponent();
        DataContext = ConnectionViewModel.Instance.Traffic;
    }
}
