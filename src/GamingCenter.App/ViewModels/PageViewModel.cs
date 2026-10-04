using GamingCenter.App.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using GamingCenter.App.Services;

namespace GamingCenter.App.ViewModels;

public enum Page { Dashboard, Stations, Sessions, Products, Customers, Credits, Sales, Expenses, Reports, Users, Settings }

/// <summary>Base for sidebar pages. Errors from services are shown as toasts rather than crashing.</summary>
public abstract partial class PageViewModel(ToastService toasts) : ObservableObject
{
    protected ToastService Toasts { get; } = toasts;

    public abstract string Title { get; }

    [ObservableProperty]
    private bool _isLoading;

    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;

    public virtual void OnNavigatedFrom() { }

    protected async Task<bool> TryAsync(Func<Task> work, string? successTitle = null, string? successMessage = null)
    {
        try
        {
            await work();
            if (successTitle is not null) Toasts.Success(successTitle, successMessage);
            return true;
        }
        catch (Exception ex)
        {
            Toasts.Error("Could not complete the action", ErrorText.For(ex));
            return false;
        }
    }

    protected async Task LoadAsync(Func<Task> work)
    {
        IsLoading = true;
        try { await TryAsync(work); }
        finally { IsLoading = false; }
    }
}

/// <summary>Named date ranges shared by History, Sales and Reports.</summary>
public static class Periods
{
    public static (DateTime From, DateTime To) Range(string period, DateTime today, DateTime? customFrom = null, DateTime? customTo = null)
    {
        today = today.Date;
        int dow = ((int)today.DayOfWeek + 6) % 7; // Monday = 0
        // A custom range picked backwards (from after to) still means the days between the two dates.
        DateTime a = (customFrom ?? today).Date, b = (customTo ?? today).Date;
        return period switch
        {
            "Yesterday" => (today.AddDays(-1), today),
            "Week" => (today.AddDays(-dow), today.AddDays(1)),
            "Month" => (new DateTime(today.Year, today.Month, 1), today.AddDays(1)),
            "Custom" => (a <= b ? a : b, (a <= b ? b : a).AddDays(1)),
            _ => (today, today.AddDays(1)),
        };
    }
}
