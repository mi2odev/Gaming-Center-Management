using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GamingCenter.App.ViewModels;

namespace GamingCenter.App.Views.Pages;

public partial class ExpensesView : UserControl
{
    public ExpensesView() => InitializeComponent();

    /// <summary>Double-clicking an expense opens it for editing (but not when the click was on its Edit/Delete buttons).</summary>
    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not Visual source) return;
        DependencyObject? d = source;
        while (d is not null and not ListBoxItem)
        {
            if (d is ButtonBase) return;
            d = VisualTreeHelper.GetParent(d);
        }
        if (d is ListBoxItem { DataContext: ExpenseRow row } && DataContext is ExpensesViewModel vm) vm.EditCommand.Execute(row);
    }
}
