using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GamingCenter.App.Services;
using GamingCenter.App.ViewModels;
using GamingCenter.Application.DTOs;

namespace GamingCenter.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => { if (Shell is { } s) s.IsCompact = ActualWidth < 1500; };
        PreviewKeyDown += OnWindowKeyDown;
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Shell?.HandleEscape() == true)
        {
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && Shell is { } shell && shell.Dialogs.Top is null)
        {
            shell.SellProductsCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnBackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DialogViewModel dialog } && dialog.CanDismiss && ReferenceEquals(Shell?.Dialogs.Top, dialog))
            dialog.Close();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (Shell is null) return;
        if (e.Key == Key.Down && SearchList.Items.Count > 0)
        {
            SearchList.SelectedIndex = 0;
            (SearchList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Shell.SearchResults.Count > 0)
        {
            Shell.OpenSearchResultCommand.Execute(Shell.SearchResults[0]);
            e.Handled = true;
        }
    }

    private void OnSearchListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SearchList.SelectedItem is SearchResult r)
        {
            Shell?.OpenSearchResultCommand.Execute(r);
            e.Handled = true;
        }
    }

    private void OnSearchResultClick(object sender, MouseButtonEventArgs e)
    {
        if (SearchList.SelectedItem is SearchResult r) Shell?.OpenSearchResultCommand.Execute(r);
    }

    private void OnNotificationsClosed(object? sender, EventArgs e)
    {
        if (Shell is { } s) s.IsNotificationsOpen = false;
    }

    private void OnUserMenuClosed(object? sender, EventArgs e)
    {
        if (Shell is { } s) s.IsUserMenuOpen = false;
    }
}
