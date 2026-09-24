using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GamingCenter.App.Services;
using GamingCenter.Application.DTOs;
using GamingCenter.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace GamingCenter.App.ViewModels;

/// <summary>Lets page view models navigate without referencing the shell.</summary>
public sealed class ShellNavigator
{
    public event Action<Page, object?>? NavigationRequested;
    public void Navigate(Page page, object? parameter = null) => NavigationRequested?.Invoke(page, parameter);
}

/// <summary>Pages that accept a parameter when navigated to (e.g. a search result).</summary>
public interface INavigationTarget
{
    void Apply(object parameter);
}

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly ISearchService _search;
    private readonly SessionWorkflow _workflow;
    private readonly ShellNavigator _nav;
    private readonly ISettingsService _settings;
    private readonly TickService _ticks;
    private readonly ThemeService _theme;
    private CancellationTokenSource? _searchCts;

    public ShellViewModel(IServiceProvider services, CurrentUserService user, ISearchService search, SessionWorkflow workflow,
        ShellNavigator nav, DialogService dialogs, ToastService toasts, NotificationCenter notifications, TickService ticks,
        ISettingsService settings, ThemeService theme)
    {
        _services = services;
        _search = search;
        _workflow = workflow;
        _nav = nav;
        _settings = settings;
        User = user;
        Dialogs = dialogs;
        Toasts = toasts;
        Notifications = notifications;
        _ticks = ticks;
        _theme = theme;
        nav.NavigationRequested += OnNavigationRequested;
        ticks.Tick += OnTick;
        settings.Changed += OnSettingsChanged;
        theme.ThemeChanged += OnThemeChanged;
        CenterName = settings.Current.CenterName;
        LogoPath = settings.Current.LogoPath;
        ClockText = DateTime.Now.ToString("ddd dd MMM · HH:mm");
    }

    private void OnNavigationRequested(Page page, object? parameter) => _ = NavigateAsync(page, parameter);
    private void OnTick(object? sender, DateTime now) => ClockText = now.ToString("ddd dd MMM · HH:mm");
    private void OnSettingsChanged(object? sender, Application.Common.AppSettings s) { CenterName = s.CenterName; LogoPath = s.LogoPath; }
    private void OnThemeChanged(object? sender, EventArgs e) => _ = NavigateAsync(CurrentPageKey, null);

    /// <summary>Detaches from app-wide services so a signed-out shell stops reacting.</summary>
    private void Detach()
    {
        _nav.NavigationRequested -= OnNavigationRequested;
        _ticks.Tick -= OnTick;
        _settings.Changed -= OnSettingsChanged;
        _theme.ThemeChanged -= OnThemeChanged;
    }

    public CurrentUserService User { get; }
    public DialogService Dialogs { get; }
    public ToastService Toasts { get; }
    public NotificationCenter Notifications { get; }

    public bool IsAdmin => User.IsAdmin;
    public string UserName => User.User?.DisplayName ?? "";
    public string UserRole => User.User?.IsAdmin == true ? "Admin" : "Operator";
    public string UserInitials => User.User?.Initials ?? "";

    [ObservableProperty] private PageViewModel? _currentPage;
    [ObservableProperty] private Page _currentPageKey = Page.Dashboard;
    [ObservableProperty] private string _clockText = "";
    [ObservableProperty] private bool _isCompact;
    [ObservableProperty] private string _centerName = "";
    [ObservableProperty] private string? _logoPath;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isSearchOpen;
    [ObservableProperty] private bool _isNotificationsOpen;
    [ObservableProperty] private bool _isUserMenuOpen;
    public ObservableCollection<SearchResult> SearchResults { get; } = [];

    public event EventHandler? SignOutRequested;

    public Task InitializeAsync() => NavigateAsync(Page.Dashboard, null);

    [RelayCommand]
    private Task Navigate(Page page) => NavigateAsync(page, null);

    private static readonly HashSet<Page> AdminPages = [Page.Stations, Page.Products, Page.Reports, Page.Users, Page.Settings];

    public async Task NavigateAsync(Page page, object? parameter)
    {
        if (AdminPages.Contains(page) && !User.IsAdmin)
        {
            Toasts.Warning("Administrator only", "Ask an admin to sign in for this page.");
            page = Page.Dashboard;
        }
        CurrentPage?.OnNavigatedFrom();
        PageViewModel vm = page switch
        {
            Page.Stations => _services.GetRequiredService<StationsViewModel>(),
            Page.Sessions => _services.GetRequiredService<SessionsViewModel>(),
            Page.Products => _services.GetRequiredService<ProductsViewModel>(),
            Page.Customers => _services.GetRequiredService<CustomersViewModel>(),
            Page.Credits => _services.GetRequiredService<CreditsViewModel>(),
            Page.Sales => _services.GetRequiredService<SalesViewModel>(),
            Page.Reports => _services.GetRequiredService<ReportsViewModel>(),
            Page.Users => _services.GetRequiredService<UsersViewModel>(),
            Page.Settings => _services.GetRequiredService<SettingsViewModel>(),
            _ => _services.GetRequiredService<DashboardViewModel>(),
        };
        CurrentPageKey = page;
        CurrentPage = vm;
        await vm.OnNavigatedToAsync();
        if (parameter is not null && vm is INavigationTarget target) target.Apply(parameter);
    }

    [RelayCommand]
    private Task StartSession() => _workflow.StartSessionAsync();

    async partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        if (string.IsNullOrWhiteSpace(value)) { SearchResults.Clear(); IsSearchOpen = false; return; }
        try
        {
            await Task.Delay(200, cts.Token);
            var results = await _search.SearchAsync(value, 8, cts.Token);
            if (cts.IsCancellationRequested) return;
            SearchResults.Clear();
            foreach (var r in results) SearchResults.Add(r);
            IsSearchOpen = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Toasts.Error("Search failed", ErrorText.For(ex)); }
    }

    [RelayCommand]
    private async Task OpenSearchResult(SearchResult? result)
    {
        if (result is null) return;
        IsSearchOpen = false;
        SearchText = "";
        switch (result.Kind)
        {
            case SearchKind.Station:
                await NavigateAsync(Page.Dashboard, null);
                await _workflow.OpenStationAsync(result.Id);
                break;
            case SearchKind.Product:
                await NavigateAsync(User.IsAdmin ? Page.Products : Page.Sales, User.IsAdmin ? $"product:{result.Title}" : null);
                break;
            case SearchKind.Customer:
                await NavigateAsync(Page.Customers, result.Id);
                break;
            case SearchKind.Session:
                await NavigateAsync(Page.Sessions, result);
                break;
        }
    }

    [RelayCommand]
    private void ToggleNotifications()
    {
        IsNotificationsOpen = !IsNotificationsOpen;
        if (IsNotificationsOpen) Notifications.MarkAllRead();
    }

    [RelayCommand]
    private void ClearNotifications()
    {
        Notifications.Items.Clear();
        Notifications.MarkAllRead();
    }

    [RelayCommand]
    private void ToggleUserMenu() => IsUserMenuOpen = !IsUserMenuOpen;

    [RelayCommand]
    private async Task ChangePassword()
    {
        IsUserMenuOpen = false;
        await Dialogs.ShowAsync(ActivatorUtilities.CreateInstance<ChangePasswordViewModel>(_services));
    }

    [RelayCommand]
    private void SignOut()
    {
        IsUserMenuOpen = false;
        CurrentPage?.OnNavigatedFrom();
        CurrentPage = null;
        Dialogs.CloseAll();
        Detach();
        SignOutRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Esc: close the top dialog or popup.</summary>
    public bool HandleEscape()
    {
        if (IsSearchOpen) { IsSearchOpen = false; return true; }
        if (IsNotificationsOpen) { IsNotificationsOpen = false; return true; }
        if (Dialogs.Top is { CanDismiss: true } top) { top.Close(); return true; }
        return false;
    }
}

public sealed partial class LoginViewModel(IAuthService auth, CurrentUserService user, ISettingsService settings) : ObservableObject
{
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;

    public string CenterName => settings.Current.CenterName;
    public string? LogoPath => settings.Current.LogoPath;
    public string Footer => $"v{typeof(LoginViewModel).Assembly.GetName().Version?.ToString(2)} · {DateTime.Today:dd MMM yyyy}";

    public event EventHandler? SignedIn;

    [RelayCommand]
    private async Task SignIn()
    {
        if (IsBusy) return;
        Error = null;
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password)) { Error = "Enter your username and password."; return; }
        IsBusy = true;
        try
        {
            var u = await auth.LoginAsync(Username, Password);
            if (u is null) { Error = "Wrong username or password, or the account is disabled."; Password = ""; return; }
            user.User = u;
            Password = "";
            SignedIn?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { Error = ErrorText.For(ex); }
        finally { IsBusy = false; }
    }
}

public sealed partial class ChangePasswordViewModel(IUserService users, ToastService toasts) : DialogViewModel
{
    [ObservableProperty] private string _current = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _confirm = "";

    [RelayCommand]
    private async Task Save()
    {
        if (NewPassword != Confirm) { Error = "The new passwords do not match."; return; }
        if (await RunAsync(() => users.ChangeOwnPasswordAsync(Current, NewPassword)))
        {
            toasts.Success("Password changed");
            Close(true);
        }
    }
}
