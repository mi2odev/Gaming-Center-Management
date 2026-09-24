using GamingCenter.Domain.Enums;

namespace GamingCenter.Domain.Entities;

public abstract class Entity
{
    public int Id { get; set; }
}

public sealed class User : Entity
{
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public UserRole Role { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>Station category such as PlayStation, PC, Xbox. Fully editable by admins.</summary>
public sealed class GamingStationType : Entity
{
    public string Name { get; set; } = "";
    /// <summary>Short label used on image placeholders, e.g. "PS", "PC".</summary>
    public string Tag { get; set; } = "";
    public decimal DefaultHourlyRate { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class GamingStation : Entity
{
    public string Name { get; set; } = "";
    public int? Number { get; set; }
    public int StationTypeId { get; set; }
    public GamingStationType? StationType { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? ImagePath { get; set; }
    public decimal HourlyRate { get; set; }
    public string? Description { get; set; }
    public string? Location { get; set; }
    /// <summary>Controllers included in <see cref="HourlyRate"/>.</summary>
    public int? ControllerCount { get; set; }
    /// <summary>Most controllers the station can take (null = same as included).</summary>
    public int? MaxControllers { get; set; }
    /// <summary>Added to the hourly rate for each controller above <see cref="ControllerCount"/>.</summary>
    public decimal ExtraControllerRate { get; set; }
    public StationState State { get; set; }
    public string? ReservedFor { get; set; }
    public DateTime? ReservedAt { get; set; }
    /// <summary>False = disabled by an admin; hidden from new sessions but kept for history.</summary>
    public bool IsActive { get; set; } = true;
    /// <summary>Soft delete. Row stays so historical sessions keep their station.</summary>
    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class PriceHistory : Entity
{
    public int StationId { get; set; }
    public GamingStation? Station { get; set; }
    public decimal OldRate { get; set; }
    public decimal NewRate { get; set; }
    public DateTime ChangedAt { get; set; }
    public int? ChangedByUserId { get; set; }
    public User? ChangedBy { get; set; }
}

public sealed class Customer : Entity
{
    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsDeleted { get; set; }
}

public sealed class ProductCategory : Entity
{
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class Product : Entity
{
    public string Name { get; set; } = "";
    public int CategoryId { get; set; }
    public ProductCategory? Category { get; set; }
    public decimal PurchasePrice { get; set; }
    public decimal SellingPrice { get; set; }
    public int Stock { get; set; }
    public int MinStock { get; set; }
    public string? ImagePath { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public bool IsLowStock => Stock <= MinStock;
}

public sealed class StockMovement : Entity
{
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int Change { get; set; }
    public int StockAfter { get; set; }
    public StockMovementReason Reason { get; set; }
    public int? SessionId { get; set; }
    public int? UserId { get; set; }
    public string? Note { get; set; }
    public DateTime At { get; set; }
}

public sealed class SessionPause : Entity
{
    public int SessionId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }

    public TimeSpan DurationAt(DateTime now) => (EndTime ?? now) - StartTime;
}

/// <summary>Rate change inside a session (controllers added or removed). Kept for the bill and history.</summary>
public sealed class SessionRateChange : Entity
{
    public int SessionId { get; set; }
    public DateTime At { get; set; }
    public decimal OldRate { get; set; }
    public decimal NewRate { get; set; }
    public int? OldControllers { get; set; }
    public int? NewControllers { get; set; }
}

/// <summary>A product line on a session. Name, price and cost are copied so history never changes.</summary>
public sealed class SessionProduct : Entity
{
    public int SessionId { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public string ProductName { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal UnitCost { get; set; }
    public DateTime AddedAt { get; set; }

    public decimal LineTotal => UnitPrice * Quantity;
    public decimal LineProfit => (UnitPrice - UnitCost) * Quantity;
}

/// <summary>Money received for a session. Payments are never deleted. One payment = one receipt.</summary>
public sealed class Payment : Entity
{
    public int SessionId { get; set; }
    public GamingSession? Session { get; set; }
    public string ReceiptNumber { get; set; } = "";
    public DateTime PaidAt { get; set; }
    public decimal GamingAmount { get; set; }
    public decimal ProductsAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public PaymentMethod Method { get; set; }
    public decimal AmountReceived { get; set; }
    public decimal ChangeGiven { get; set; }
    public int? UserId { get; set; }
    public User? User { get; set; }
}

public sealed class ApplicationSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
