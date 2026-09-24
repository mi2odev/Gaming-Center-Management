namespace GamingCenter.Domain.Enums;

/// <summary>Operational state set by staff. Occupied/Paused are derived from the running session.</summary>
public enum StationState
{
    Available = 0,
    Reserved = 1,
    Maintenance = 2,
}

/// <summary>What the dashboard shows for a station, combining admin state and the live session.</summary>
public enum StationDisplayStatus
{
    Available,
    Occupied,
    Paused,
    Reserved,
    Offline,
    TimeUp,
}

public enum SessionMode
{
    Open = 0,
    FixedDuration = 1,
    FixedBudget = 2,
    /// <summary>Product-only sale at the counter, no gaming station.</summary>
    CounterSale = 3,
}

public enum SessionStatus
{
    Running = 0,
    Paused = 1,
    /// <summary>Play time stopped (auto-end or operator), bill not paid yet.</summary>
    AwaitingPayment = 2,
    Completed = 3,
    Cancelled = 4,
}

public enum PaymentMethod
{
    Cash = 0,
    Card = 1,
    Other = 2,
}

public enum UserRole
{
    Operator = 0,
    Admin = 1,
}

public enum RoundingMode
{
    Up = 0,
    Nearest = 1,
    Down = 2,
}

public enum StockMovementReason
{
    Sale = 0,
    SaleReturn = 1,
    Restock = 2,
    Adjustment = 3,
    Initial = 4,
}
