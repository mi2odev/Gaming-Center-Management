using GamingCenter.Domain.Enums;

namespace GamingCenter.Application.DTOs;

public sealed record UserDto(int Id, string Username, string DisplayName, UserRole Role, bool IsActive, DateTime? LastLoginAt)
{
    public bool IsAdmin => Role == UserRole.Admin;
    public string Initials => string.Concat(DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));
}

public sealed record SaveUserRequest(int? Id, string Username, string DisplayName, UserRole Role, bool IsActive, string? NewPassword);

public sealed record StationTypeDto(int Id, string Name, string Tag, decimal DefaultHourlyRate, int SortOrder, bool IsActive, int StationCount);

public sealed record SaveStationTypeRequest(int? Id, string Name, string Tag, decimal DefaultHourlyRate, bool IsActive);

public sealed record StationDto(
    int Id,
    string Name,
    int? Number,
    int StationTypeId,
    string TypeName,
    string TypeTag,
    string? Brand,
    string? Model,
    string? ImagePath,
    decimal HourlyRate,
    string? Description,
    string? Location,
    int? ControllerCount,
    int? MaxControllers,
    decimal ExtraControllerRate,
    StationState State,
    string? ReservedFor,
    DateTime? ReservedAt,
    bool IsActive)
{
    /// <summary>Short label for image placeholders: model if short, else the type tag.</summary>
    public string Tag => !string.IsNullOrWhiteSpace(Model) && Model.Length <= 5 ? Model.ToUpperInvariant() : TypeTag;

    /// <summary>True when the operator chooses how many controllers (and the price changes with it).</summary>
    public bool HasControllerPricing => Domain.Billing.ControllerPricing.IsPriced(ControllerCount, MaxControllers, ExtraControllerRate);

    public decimal RateFor(int? controllers) =>
        Domain.Billing.ControllerPricing.RateFor(HourlyRate, ControllerCount, ExtraControllerRate, controllers);
}

public sealed record SaveStationRequest(
    int? Id,
    string Name,
    int? Number,
    int StationTypeId,
    string? Brand,
    string? Model,
    string? ImagePath,
    decimal HourlyRate,
    string? Description,
    string? Location,
    int? ControllerCount,
    StationState State,
    bool IsActive,
    int? MaxControllers = null,
    decimal ExtraControllerRate = 0);

public sealed record PriceHistoryDto(DateTime ChangedAt, decimal OldRate, decimal NewRate, string? ChangedBy);

public sealed record ProductCategoryDto(int Id, string Name, int SortOrder, bool IsActive, int ProductCount);

public sealed record ProductDto(
    int Id,
    string Name,
    int CategoryId,
    string CategoryName,
    decimal PurchasePrice,
    decimal SellingPrice,
    int Stock,
    int MinStock,
    string? ImagePath,
    bool IsActive)
{
    public bool IsLowStock => Stock <= MinStock;
    public decimal Margin => SellingPrice - PurchasePrice;
}

public sealed record SaveProductRequest(
    int? Id,
    string Name,
    int CategoryId,
    decimal PurchasePrice,
    decimal SellingPrice,
    int Stock,
    int MinStock,
    string? ImagePath,
    bool IsActive);

public sealed record CustomerDto(int Id, string Name, string? Phone, string? Notes, int TotalSessions, decimal TotalSpent, DateTime? LastVisit, DateTime CreatedAt);

public sealed record SaveCustomerRequest(int? Id, string Name, string? Phone, string? Notes);

public sealed record StartSessionRequest(int StationId, int? CustomerId, SessionMode Mode, int? PlannedMinutes, decimal? Budget, int? Controllers = null);

public sealed record CompleteSessionRequest(
    int SessionId,
    DateTime EndTime,
    PaymentMethod Method,
    decimal AmountReceived,
    int? CustomerId);

public sealed record CartLine(int ProductId, int Quantity);

public sealed record CounterSaleRequest(IReadOnlyList<CartLine> Lines, PaymentMethod Method, decimal AmountReceived, int? CustomerId);

public sealed record HistoryRow(
    int SessionId,
    string? ReceiptNumber,
    string StationName,
    string CustomerName,
    SessionMode Mode,
    SessionStatus Status,
    DateTime StartTime,
    DateTime? EndTime,
    long PlayedSeconds,
    decimal GamingTotal,
    decimal ProductsTotal,
    decimal Total,
    PaymentMethod? Method,
    DateTime? PaidAt,
    string? Operator);

public sealed record ReceiptLine(string Name, int Quantity, decimal UnitPrice, decimal LineTotal);

public sealed record ReceiptDto(
    int SessionId,
    string ReceiptNumber,
    DateTime IssuedAt,
    string CenterName,
    string Address,
    string Phone,
    string StationName,
    string? CustomerName,
    SessionMode Mode,
    DateTime StartTime,
    DateTime? EndTime,
    long PlayedSeconds,
    decimal HourlyRate,
    string BillingLabel,
    string? RateNote,
    decimal GamingTotal,
    IReadOnlyList<ReceiptLine> Lines,
    decimal ProductsTotal,
    decimal Total,
    PaymentMethod Method,
    decimal AmountReceived,
    decimal Change,
    string? Operator,
    IReadOnlyList<(DateTime Start, DateTime? End)> Pauses,
    string Footer);

public sealed record PaymentRow(
    int PaymentId,
    int SessionId,
    string ReceiptNumber,
    DateTime PaidAt,
    string StationName,
    string CustomerName,
    decimal GamingAmount,
    decimal ProductsAmount,
    decimal TotalAmount,
    PaymentMethod Method,
    string? Operator);

public sealed record DashboardStats(
    decimal Revenue,
    decimal RevenueYesterday,
    decimal GamingRevenue,
    decimal ProductRevenue,
    decimal ProductProfit,
    int Sessions,
    TimeSpan AverageSession,
    string? MostUsedStation,
    TimeSpan MostUsedStationTime,
    string? MostSoldProduct,
    int MostSoldProductUnits,
    IReadOnlyList<ProductDto> LowStock);

public sealed record DayRevenue(DateTime Day, string Label, decimal Gaming, decimal Products)
{
    public decimal Total => Gaming + Products;
}

public sealed record StationRevenue(string Name, decimal Revenue, TimeSpan PlayTime, int Sessions);

public sealed record ProductSales(string Name, int Units, decimal Revenue, decimal Profit);

public sealed record ReportData(
    DateTime From,
    DateTime To,
    decimal TotalRevenue,
    decimal GamingRevenue,
    decimal ProductRevenue,
    decimal ProductProfit,
    int Sessions,
    TimeSpan AverageSession,
    decimal PreviousTotalRevenue,
    IReadOnlyList<DayRevenue> PerDay,
    IReadOnlyList<StationRevenue> PerStation,
    IReadOnlyList<ProductSales> TopProducts,
    IReadOnlyDictionary<SessionMode, int> ModeCounts,
    IReadOnlyDictionary<PaymentMethod, decimal> MethodTotals,
    int? BusiestHour);

public enum SearchKind { Station, Product, Customer, Session }

public sealed record SearchResult(SearchKind Kind, int Id, string Title, string Subtitle);
