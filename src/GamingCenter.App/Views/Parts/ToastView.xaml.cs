using System.Windows;
using System.Windows.Controls;
using GamingCenter.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GamingCenter.App.Views.Parts;

public partial class ToastView : UserControl
{
    public ToastView() => InitializeComponent();

    private void OnDismiss(object sender, RoutedEventArgs e)
    {
        if (DataContext is Toast t) App.Services.GetRequiredService<ToastService>().Dismiss(t);
    }
}
