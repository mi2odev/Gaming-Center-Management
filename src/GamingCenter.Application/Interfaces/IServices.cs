using GamingCenter.Application.Common;
using GamingCenter.Application.DTOs;
using GamingCenter.Domain.Entities;
using GamingCenter.Domain.Enums;

namespace GamingCenter.Application.Interfaces;

public interface IClock
{
    DateTime Now { get; }
}

/// <summary>The signed-in user. Services use it for auditing and role checks.</summary>
public interface ICurrentUser
{
    UserDto? User { get; }
    bool IsAdmin => User?.IsAdmin == true;
}

/// <summary>Thrown when an operation is refused for a business reason; the message is shown to the operator.</summary>
public sealed class BusinessException(string message) : Exception(message);

public interface IAuthService
{
    Task<UserDto?> LoginAsync(string username, string password, CancellationToken ct = default);
}

public interface IUserService
{
    Task<IReadOnlyList<UserDto>> GetAllAsync(CancellationToken ct = default);
    Task<UserDto> SaveAsync(SaveUserRequest request, CancellationToken ct = default);
    Task ChangeOwnPasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default);
}

public interface IStationService
{
    Task<IReadOnlyList<StationDto>> GetAllAsync(bool includeInactive = true, CancellationToken ct = default);
    Task<StationDto?> GetAsync(int id, CancellationToken ct = default);
    Task<StationDto> SaveAsync(SaveStationRequest request, CancellationToken ct = default);
    Task SetActiveAsync(int id, bool active, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task SetStateAsync(int id, StationState state, string? reservedFor = null, DateTime? reservedAt = null, CancellationToken ct = default);
    Task<IReadOnlyList<PriceHistoryDto>> GetPriceHistoryAsync(int stationId, CancellationToken ct = default);

    Task<IReadOnlyList<StationTypeDto>> GetTypesAsync(CancellationToken ct = default);
    Task<StationTypeDto> SaveTypeAsync(SaveStationTypeRequest request, CancellationToken ct = default);
    Task DeleteTypeAsync(int id, CancellationToken ct = default);
}

public interface ISessionService
{
    /// <summary>Running, paused and awaiting-payment sessions with pauses and products loaded.</summary>
    Task<IReadOnlyList<GamingSession>> GetLiveSessionsAsync(CancellationToken ct = default);
    Task<GamingSession?> GetAsync(int sessionId, CancellationToken ct = default);

    Task<GamingSession> StartAsync(StartSessionRequest request, CancellationToken ct = default);
    Task<GamingSession> PauseAsync(int sessionId, CancellationToken ct = default);
    Task<GamingSession> ResumeAsync(int sessionId, CancellationToken ct = default);
    Task<GamingSession> ExtendTimeAsync(int sessionId, int minutes, CancellationToken ct = default);
    Task<GamingSession> AddBudgetAsync(int sessionId, decimal amount, CancellationToken ct = default);
    Task<GamingSession> ChangeModeAsync(int sessionId, SessionMode mode, int? plannedMinutes, decimal? budget, CancellationToken ct = default);
    /// <summary>Changes the number of controllers; the new hourly rate applies from now on.</summary>
    Task<GamingSession> ChangeControllersAsync(int sessionId, int controllers, CancellationToken ct = default);
    Task<GamingSession> AttachCustomerAsync(int sessionId, int? customerId, CancellationToken ct = default);

    Task<GamingSession> AddProductAsync(int sessionId, int productId, int quantity = 1, CancellationToken ct = default);
    Task<GamingSession> SetProductQuantityAsync(int sessionProductId, int quantity, CancellationToken ct = default);

    /// <summary>Stops the clock without payment (auto-end when time expires).</summary>
    Task<GamingSession> StopPlayAsync(int sessionId, DateTime endTime, CancellationToken ct = default);

    /// <summary>Closes the session, records the payment and frees the station in one transaction.</summary>
    Task<Payment> CompleteAsync(CompleteSessionRequest request, CancellationToken ct = default);
    Task CancelAsync(int sessionId, string reason, CancellationToken ct = default);

    Task<Payment> CounterSaleAsync(CounterSaleRequest request, CancellationToken ct = default);
}

public interface IProductService
{
    Task<IReadOnlyList<ProductDto>> GetAllAsync(bool includeInactive = true, CancellationToken ct = default);
    Task<ProductDto> SaveAsync(SaveProductRequest request, CancellationToken ct = default);
    Task SetActiveAsync(int id, bool active, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task ReceiveStockAsync(int productId, int quantity, string? note, CancellationToken ct = default);
    Task AdjustStockAsync(int productId, int newStock, string? note, CancellationToken ct = default);
    Task<IReadOnlyList<ProductDto>> GetLowStockAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ProductCategoryDto>> GetCategoriesAsync(CancellationToken ct = default);
    Task<ProductCategoryDto> SaveCategoryAsync(int? id, string name, bool isActive, CancellationToken ct = default);
    Task DeleteCategoryAsync(int id, CancellationToken ct = default);
}

public interface ICustomerService
{
    Task<IReadOnlyList<CustomerDto>> GetAllAsync(string? search = null, CancellationToken ct = default);
    Task<CustomerDto> SaveAsync(SaveCustomerRequest request, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}

public interface ICreditService
{
    /// <summary>Customers with a non-zero balance (or all customers with any credit history).</summary>
    Task<IReadOnlyList<CreditBalanceDto>> GetBalancesAsync(bool includeSettled = false, CancellationToken ct = default);
    Task<IReadOnlyList<CreditEntryDto>> GetHistoryAsync(int customerId, CancellationToken ct = default);
    Task<decimal> GetBalanceAsync(int customerId, CancellationToken ct = default);
    Task<decimal> GetTotalOutstandingAsync(CancellationToken ct = default);
    /// <summary>Customer pays back part or all of what they owe.</summary>
    Task<CreditEntryDto> RecordRepaymentAsync(int customerId, decimal amount, PaymentMethod method, string? note, CancellationToken ct = default);
    /// <summary>Admin: add a debt by hand (e.g. from before the app). Negative amount forgives debt.</summary>
    Task<CreditEntryDto> AddManualAsync(int customerId, decimal amount, string note, CancellationToken ct = default);
}

public interface IReportService
{
    Task<DashboardStats> GetDashboardStatsAsync(DateTime day, CancellationToken ct = default);
    Task<IReadOnlyList<HistoryRow>> GetHistoryAsync(DateTime from, DateTime to, string? search = null, CancellationToken ct = default);
    Task<ReceiptDto?> GetReceiptAsync(int sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<PaymentRow>> GetPaymentsAsync(DateTime from, DateTime to, CancellationToken ct = default);
    Task<ReportData> GetReportAsync(DateTime from, DateTime to, bool groupByMonth, CancellationToken ct = default);
}

public interface ISearchService
{
    Task<IReadOnlyList<SearchResult>> SearchAsync(string text, int limit = 20, CancellationToken ct = default);
}

public interface ISettingsService
{
    AppSettings Current { get; }
    event EventHandler<AppSettings>? Changed;
    Task LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}

public interface IBackupService
{
    string DatabasePath { get; }
    string DefaultBackupFolder { get; }
    Task<string> BackupNowAsync(string? folder = null, CancellationToken ct = default);
    /// <summary>Replaces the live database with a backup. A safety backup of the current data is taken first.</summary>
    Task RestoreAsync(string backupFile, CancellationToken ct = default);
    Task<string?> RunAutomaticBackupIfDueAsync(CancellationToken ct = default);
    IReadOnlyList<FileInfo> ListBackups(string? folder = null);
}

/// <summary>Stores station/product images on disk (resized) and returns a path relative to the data folder.</summary>
public interface IImageStore
{
    Task<string> ImportAsync(string sourceFile, string category, CancellationToken ct = default);
    string? Resolve(string? relativePath);
}

public interface IDataPaths
{
    string DataFolder { get; }
    string DatabasePath { get; }
    string ImagesFolder { get; }
    string BackupsFolder { get; }
}
