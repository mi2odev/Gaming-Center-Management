using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GamingCenter.App.ViewModels;

namespace GamingCenter.App.Views.Pages;

public partial class StationsView : UserControl
{
    public StationsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged o) o.PropertyChanged -= OnVmChanged;
            if (e.NewValue is INotifyPropertyChanged n) n.PropertyChanged += OnVmChanged;
        };
    }

    // "Price" action focuses the hourly price field.
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StationsViewModel.FocusPrice) && sender is StationsViewModel { FocusPrice: true })
            Dispatcher.BeginInvoke(() => { PriceBox.Focus(); PriceBox.SelectAll(); }, DispatcherPriority.Input);
    }
}
