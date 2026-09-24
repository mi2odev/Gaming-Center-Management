using GamingCenter.Application.Interfaces;
using GamingCenter.Infrastructure.Data;
using GamingCenter.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GamingCenter.Infrastructure;

public sealed class DataPaths : IDataPaths
{
    public DataPaths(string dataFolder)
    {
        DataFolder = Path.GetFullPath(dataFolder);
        Directory.CreateDirectory(DataFolder);
        Directory.CreateDirectory(ImagesFolder);
        Directory.CreateDirectory(BackupsFolder);
    }

    public string DataFolder { get; }
    public string DatabasePath => Path.Combine(DataFolder, "gamingcenter.db");
    public string ImagesFolder => Path.Combine(DataFolder, "images");
    public string BackupsFolder => Path.Combine(DataFolder, "backups");

    /// <summary>%LOCALAPPDATA%\mi2oGamingCenter</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mi2oGamingCenter");
}

public static class DependencyInjection
{
    /// <summary>Registers the database and all business services. The host must also register <see cref="ICurrentUser"/>.</summary>
    public static IServiceCollection AddGamingCenterInfrastructure(this IServiceCollection services, string dataFolder)
    {
        var paths = new DataPaths(dataFolder);
        services.AddSingleton<IDataPaths>(paths);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            DefaultTimeout = 15,
        }.ToString();
        services.AddDbContextFactory<GamingCenterDbContext>(o => o.UseSqlite(connectionString));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<IUserService, UserService>();
        services.AddSingleton<IStationService, StationService>();
        services.AddSingleton<ISessionService, SessionService>();
        services.AddSingleton<IProductService, ProductService>();
        services.AddSingleton<ICustomerService, CustomerService>();
        services.AddSingleton<ICreditService, CreditService>();
        services.AddSingleton<IReportService, ReportService>();
        services.AddSingleton<ISearchService, SearchService>();
        services.AddSingleton<IBackupService, BackupService>();
        return services;
    }
}
