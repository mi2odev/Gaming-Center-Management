using GamingCenter.Domain.Billing;
using GamingCenter.Domain.Enums;

namespace GamingCenter.Domain.Entities;

/// <summary>
/// One customer visit on one station (or a counter sale when <see cref="StationId"/> is null).
/// All time maths is derived from stored timestamps so timers survive restarts.
/// </summary>
public sealed class GamingSession : Entity
{
    public int? StationId { get; set; }
    public GamingStation? Station { get; set; }
    /// <summary>Station name at session start, so renamed/deleted stations still read correctly.</summary>
    public string StationName { get; set; } = "";
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public SessionMode Mode { get; set; }
    public SessionStatus Status { get; set; }
    public DateTime StartTime { get; set; }
    /// <summary>When play stopped. Set when billed or auto-ended.</summary>
    public DateTime? EndTime { get; set; }

    // Price and billing rules locked at start
    public decimal HourlyRate { get; set; }
    public int BillingUnitSeconds { get; set; }
    public RoundingMode Rounding { get; set; }
    public int MinimumChargeMinutes { get; set; }
    public decimal MoneyRoundingStep { get; set; }

    /// <summary>Fixed-duration mode: total purchased minutes (including extensions).</summary>
    public int? PlannedMinutes { get; set; }
    /// <summary>Fixed-budget mode: total money given (including top-ups).</summary>
    public decimal? Budget { get; set; }

    // Final figures, written on completion
    public long PlayedSeconds { get; set; }
    public decimal GamingTotal { get; set; }
    public decimal ProductsTotal { get; set; }
    public decimal Total { get; set; }

    public int? StartedByUserId { get; set; }
    public User? StartedBy { get; set; }
    public int? EndedByUserId { get; set; }
    public string? Notes { get; set; }

    public List<SessionPause> Pauses { get; set; } = [];
    public List<SessionProduct> Products { get; set; } = [];
    public Payment? Payment { get; set; }

    public BillingRules Rules => new(BillingUnitSeconds, Rounding, MinimumChargeMinutes, MoneyRoundingStep);

    public bool IsCounterSale => Mode == SessionMode.CounterSale;
    public bool IsLive => Status is SessionStatus.Running or SessionStatus.Paused or SessionStatus.AwaitingPayment;

    public void ApplyRules(BillingRules rules)
    {
        BillingUnitSeconds = rules.UnitSeconds;
        Rounding = rules.Rounding;
        MinimumChargeMinutes = rules.MinimumChargeMinutes;
        MoneyRoundingStep = rules.MoneyRoundingStep;
    }

    /// <summary>The instant the clock is evaluated at: end time if play stopped, otherwise now.</summary>
    public DateTime ClockAt(DateTime now) => EndTime ?? now;

    public TimeSpan PausedTime(DateTime now)
    {
        var at = ClockAt(now);
        var total = TimeSpan.Zero;
        foreach (var p in Pauses)
        {
            var end = p.EndTime ?? at;
            if (end > p.StartTime) total += end - p.StartTime;
        }
        return total;
    }

    public TimeSpan WallTime(DateTime now) => ClockAt(now) - StartTime;

    /// <summary>Actual gaming time: wall time minus pauses.</summary>
    public TimeSpan PlayedTime(DateTime now)
    {
        if (IsCounterSale) return TimeSpan.Zero;
        var played = WallTime(now) - PausedTime(now);
        return played < TimeSpan.Zero ? TimeSpan.Zero : played;
    }

    /// <summary>Play time allowed by the fixed duration or budget; null for open sessions.</summary>
    public TimeSpan? AllowedTime => Mode switch
    {
        SessionMode.FixedDuration => TimeSpan.FromMinutes(PlannedMinutes ?? 0),
        SessionMode.FixedBudget => BillingCalculator.TimeForBudget(Budget ?? 0, HourlyRate),
        _ => null,
    };

    public TimeSpan? RemainingTime(DateTime now)
    {
        if (AllowedTime is not { } allowed) return null;
        var left = allowed - PlayedTime(now);
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    public bool IsTimeUp(DateTime now) => AllowedTime is { } allowed && PlayedTime(now) >= allowed;

    /// <summary>Overtime past the allowed time (fixed modes), otherwise zero.</summary>
    public TimeSpan Overtime(DateTime now)
    {
        if (AllowedTime is not { } allowed) return TimeSpan.Zero;
        var over = PlayedTime(now) - allowed;
        return over > TimeSpan.Zero ? over : TimeSpan.Zero;
    }

    /// <summary>0..1 share of allowed time used, for progress bars.</summary>
    public double Progress(DateTime now)
    {
        if (AllowedTime is not { } allowed || allowed <= TimeSpan.Zero) return 0;
        return Math.Clamp(PlayedTime(now) / allowed, 0, 1);
    }

    /// <summary>
    /// Gaming charge according to the mode:
    /// open = played time; fixed duration = purchased time (or played time if the customer overstayed);
    /// fixed budget = played time capped at the budget.
    /// </summary>
    public decimal GamingCost(DateTime now)
    {
        if (IsCounterSale) return 0m;
        var rules = Rules;
        var played = PlayedTime(now);
        var actual = BillingCalculator.Cost(played, HourlyRate, rules);
        return Mode switch
        {
            SessionMode.FixedDuration => Math.Max(
                BillingCalculator.Cost(TimeSpan.FromMinutes(PlannedMinutes ?? 0), HourlyRate, rules), actual),
            SessionMode.FixedBudget => Math.Min(actual, Budget ?? 0m),
            _ => actual,
        };
    }

    public decimal ProductsCost() => Products.Sum(p => p.LineTotal);

    public decimal TotalCost(DateTime now) => GamingCost(now) + ProductsCost();

    public SessionPause? OpenPause => Pauses.FirstOrDefault(p => p.EndTime is null);
}
