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

    // Price and billing rules locked at start. HourlyRate is the rate in force now;
    // earlier rates are kept in RateChanges.
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

    /// <summary>Number of controllers currently in use (null when the station does not price by controller).</summary>
    public int? Controllers { get; set; }

    public List<SessionPause> Pauses { get; set; } = [];
    public List<SessionRateChange> RateChanges { get; set; } = [];
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
    public TimeSpan PlayedTime(DateTime now) => PlayedUntil(ClockAt(now), now);

    /// <summary>Play time between the start and <paramref name="instant"/> (pauses excluded).</summary>
    private TimeSpan PlayedUntil(DateTime instant, DateTime now)
    {
        if (IsCounterSale) return TimeSpan.Zero;
        var clock = ClockAt(now);
        if (instant > clock) instant = clock;
        if (instant <= StartTime) return TimeSpan.Zero;
        var paused = TimeSpan.Zero;
        foreach (var p in Pauses)
        {
            var pEnd = p.EndTime ?? clock;
            if (pEnd > instant) pEnd = instant;
            if (pEnd > p.StartTime) paused += pEnd - p.StartTime;
        }
        var played = instant - StartTime - paused;
        return played < TimeSpan.Zero ? TimeSpan.Zero : played;
    }

    /// <summary>
    /// Play time split by hourly rate. The rate changes when controllers are added or removed
    /// mid-session; each part is priced at the rate that applied while it was played.
    /// </summary>
    public IReadOnlyList<RateSegment> RateSegments(DateTime now)
    {
        var segments = new List<RateSegment>();
        var changes = RateChanges.OrderBy(c => c.At).ToList();
        var from = StartTime;
        decimal rate = changes.Count > 0 ? changes[0].OldRate : HourlyRate;
        int? controllers = changes.Count > 0 ? changes[0].OldControllers : Controllers;
        foreach (var c in changes)
        {
            Add(from, c.At, rate, controllers);
            from = c.At;
            rate = c.NewRate;
            controllers = c.NewControllers;
        }
        Add(from, ClockAt(now), rate, controllers);
        return segments;

        void Add(DateTime a, DateTime b, decimal r, int? ctrl)
        {
            var secs = PlayedUntil(b, now) - PlayedUntil(a, now);
            if (secs > TimeSpan.Zero) segments.Add(new RateSegment(a, b, r, ctrl, secs));
        }
    }

    /// <summary>Exact (unrounded) gaming value: Σ rate × time over all rate segments.</summary>
    private decimal ExactValue(DateTime now) =>
        RateSegments(now).Sum(s => s.HourlyRate * (decimal)Math.Floor(s.Played.TotalSeconds) / 3600m);

    /// <summary>Time-weighted hourly rate over what has been played (current rate if nothing played yet).</summary>
    public decimal AverageRate(DateTime now)
    {
        if (RateChanges.Count == 0) return HourlyRate;
        var secs = (decimal)Math.Floor(PlayedTime(now).TotalSeconds);
        return secs <= 0 ? HourlyRate : ExactValue(now) * 3600m / secs;
    }

    /// <summary>Play time allowed by the fixed duration or budget; null for open sessions.</summary>
    public TimeSpan? AllowedTime(DateTime now)
    {
        switch (Mode)
        {
            case SessionMode.FixedDuration:
                return TimeSpan.FromMinutes(PlannedMinutes ?? 0);
            case SessionMode.FixedBudget:
                if (RateChanges.Count == 0) return BillingCalculator.TimeForBudget(Budget ?? 0, HourlyRate);
                // What is left of the budget lasts at the current rate.
                var left = (Budget ?? 0) - ExactValue(now);
                var extra = left > 0 ? BillingCalculator.TimeForBudget(left, HourlyRate) : TimeSpan.Zero;
                return PlayedTime(now) + extra;
            default:
                return null;
        }
    }

    public TimeSpan? RemainingTime(DateTime now)
    {
        if (AllowedTime(now) is not { } allowed) return null;
        var left = allowed - PlayedTime(now);
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    public bool IsTimeUp(DateTime now) => AllowedTime(now) is { } allowed && PlayedTime(now) >= allowed;

    /// <summary>Overtime past the allowed time (fixed modes), otherwise zero.</summary>
    public TimeSpan Overtime(DateTime now)
    {
        if (AllowedTime(now) is not { } allowed) return TimeSpan.Zero;
        var over = PlayedTime(now) - allowed;
        return over > TimeSpan.Zero ? over : TimeSpan.Zero;
    }

    /// <summary>0..1 share of allowed time used, for progress bars.</summary>
    public double Progress(DateTime now)
    {
        if (AllowedTime(now) is not { } allowed || allowed <= TimeSpan.Zero) return 0;
        return Math.Clamp(PlayedTime(now) / allowed, 0, 1);
    }

    /// <summary>
    /// Gaming charge according to the mode:
    /// open = played time; fixed duration = purchased time (or played time if the customer overstayed);
    /// fixed budget = played time capped at the budget.
    /// With controller changes, time is priced at the time-weighted average of the rates actually played.
    /// </summary>
    public decimal GamingCost(DateTime now)
    {
        if (IsCounterSale) return 0m;
        var rules = Rules;
        var played = PlayedTime(now);
        var avg = AverageRate(now);
        var actual = BillingCalculator.Cost(played, avg, rules);
        switch (Mode)
        {
            case SessionMode.FixedDuration:
                var planned = TimeSpan.FromMinutes(PlannedMinutes ?? 0);
                // Unplayed purchased time is charged at the current rate.
                decimal plannedRate = HourlyRate;
                if (played > TimeSpan.Zero && planned > TimeSpan.Zero)
                {
                    var playedSecs = (decimal)Math.Floor(Math.Min(played.TotalSeconds, planned.TotalSeconds));
                    var restSecs = (decimal)planned.TotalSeconds - playedSecs;
                    plannedRate = (avg * playedSecs + HourlyRate * restSecs) / (decimal)planned.TotalSeconds;
                }
                return Math.Max(BillingCalculator.Cost(planned, plannedRate, rules), actual);
            case SessionMode.FixedBudget:
                return Math.Min(actual, Budget ?? 0m);
            default:
                return actual;
        }
    }

    public decimal ProductsCost() => Products.Sum(p => p.LineTotal);

    public decimal TotalCost(DateTime now) => GamingCost(now) + ProductsCost();

    public SessionPause? OpenPause => Pauses.FirstOrDefault(p => p.EndTime is null);
}

/// <summary>A part of a session played at one hourly rate.</summary>
public sealed record RateSegment(DateTime From, DateTime To, decimal HourlyRate, int? Controllers, TimeSpan Played);
