using System.IO;
using System.Windows;
using System.Windows.Threading;
using GamingCenter.App.Services;
using GamingCenter.App.ViewModels;
using GamingCenter.App.Views;
using GamingCenter.Application.Interfaces;
using GamingCenter.Infrastructure;
using GamingCenter.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GamingCenter.App;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private Mutex? _singleInstance;
    private ILogger<App>? _logger;

    public static IServiceProvider Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One instance per laptop: two copies would both run timers and alerts on the same database.
        _singleInstance = new Mutex(false, "Local\\mi2oGamingCenter");
        bool owned;
        try { owned = _singleInstance.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            MessageBox.Show("mi2o Gaming Center is already running.", "mi2o Gaming Center", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => { _logger?.LogError(args.Exception, "Unobserved task exception"); args.SetObserved(); };

        try
        {
            _host = BuildHost();
            Services = _host.Services;
            _logger = Services.GetRequiredService<ILogger<App>>();

            var config = Services.GetRequiredService<IConfiguration>();
            await using (var db = await Services.GetRequiredService<IDbContextFactory<GamingCenterDbContext>>().CreateDbContextAsync())
                await DbInitializer.InitializeAsync(db, config.GetValue("Database:SeedDemoData", true));

            await Services.GetRequiredService<ISettingsService>().LoadAsync();
            Services.GetRequiredService<ThemeService>().ApplyFromSettings();
            Services.GetRequiredService<NotificationCenter>(); // starts watching sessions
            Services.GetRequiredService<MaintenanceService>().Start();
            _logger.LogInformation("Started. Data folder: {Folder}", Services.GetRequiredService<IDataPaths>().DataFolder);

            ShowLogin();
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "Startup failed");
            MessageBox.Show($"The application could not start.\n\n{ex.Message}", "mi2o Gaming Center", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
            Args = Environment.GetCommandLineArgs().Skip(1).ToArray(),
        });
        builder.Configuration.AddJsonFile("appsettings.json", optional: true);

        var configured = builder.Configuration["Storage:DataFolder"];
        var dataFolder = string.IsNullOrWhiteSpace(configured)
            ? DataPaths.DefaultFolder
            : Environment.ExpandEnvironmentVariables(configured);

        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(dataFolder, "logs")));

        var s = builder.Services;
        s.AddGamingCenterInfrastructure(dataFolder);
        s.AddSingleton<CurrentUserService>();
        s.AddSingleton<ICurrentUser>(sp => sp.GetRequiredService<CurrentUserService>());
        s.AddSingleton<IImageStore, WpfImageStore>();
        s.AddSingleton<TickService>();
        s.AddSingleton<ToastService>();
        s.AddSingleton<DialogService>();
        s.AddSingleton<ThemeService>();
        s.AddSingleton<LiveSessionStore>();
        s.AddSingleton<NotificationCenter>();
        s.AddSingleton<MaintenanceService>();
        s.AddSingleton<SessionWorkflow>();
        s.AddSingleton<PrintService>();
        s.AddSingleton<FileDialogService>();
        s.AddSingleton<ShellNavigator>();

        s.AddTransient<LoginViewModel>();
        s.AddTransient<ShellViewModel>();
        s.AddTransient<DashboardViewModel>();
        s.AddTransient<StationsViewModel>();
        s.AddTransient<SessionsViewModel>();
        s.AddTransient<ProductsViewModel>();
        s.AddTransient<CustomersViewModel>();
        s.AddTransient<CreditsViewModel>();
        s.AddTransient<SalesViewModel>();
        s.AddTransient<ReportsViewModel>();
        s.AddTransient<UsersViewModel>();
        s.AddTransient<SettingsViewModel>();
        return builder.Build();
    }

    private void ShowLogin()
    {
        var vm = Services.GetRequiredService<LoginViewModel>();
        var window = new LoginWindow { DataContext = vm };
        vm.SignedIn += async (_, _) =>
        {
            await ShowMainAsync();
            window.Close();
        };
        window.Closed += (_, _) =>
        {
            if (Services.GetRequiredService<CurrentUserService>().User is null) Shutdown();
        };
        window.Show();
    }

    private async Task ShowMainAsync()
    {
        var shell = Services.GetRequiredService<ShellViewModel>();
        var window = new MainWindow { DataContext = shell };
        shell.SignOutRequested += (_, _) =>
        {
            Services.GetRequiredService<CurrentUserService>().User = null;
            ShowLogin();
            window.Close();
        };
        window.Closed += (_, _) =>
        {
            if (Services.GetRequiredService<CurrentUserService>().User is not null) Shutdown();
        };
        MainWindow = window;
        window.Show();
        await shell.InitializeAsync();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI exception");
        Services?.GetService<ToastService>()?.Error("Something went wrong", ErrorText.For(e.Exception));
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _host?.Services.GetService<MaintenanceService>()?.Stop();
            _host?.Dispose();
        }
        finally
        {
            try { _singleInstance?.ReleaseMutex(); } catch (ApplicationException) { }
            _singleInstance?.Dispose();
            base.OnExit(e);
        }
    }
}
