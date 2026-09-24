using GamingCenter.Domain.Enums;

namespace GamingCenter.Domain.Billing;

/// <summary>
/// How play time is turned into money. A copy is stored on every session at start,
/// so later changes to settings never alter a running or historical session.
/// </summary>
/// <param name="UnitSeconds">Billing unit. 0 means exact (per-second) billing; 60 = per minute, 900 = per 15 minutes, 3600 = per hour.</param>
/// <param name="Rounding">How partial units are rounded.</param>
/// <param name="MinimumChargeMinutes">Any non-zero play time is billed at least this long.</param>
/// <param name="MoneyRoundingStep">Final amount is rounded to a multiple of this (e.g. 1, 5 or 10 DA). 0 disables rounding.</param>
public sealed record BillingRules(int UnitSeconds, RoundingMode Rounding, int MinimumChargeMinutes, decimal MoneyRoundingStep)
{
    public static BillingRules Default { get; } = new(60, RoundingMode.Up, 0, 1m);

    public static readonly int[] SupportedUnits = [0, 60, 300, 900, 3600];

    public string UnitLabel => UnitSeconds switch
    {
        0 => "exact time",
        60 => "per minute",
        300 => "per 5 minutes",
        900 => "per 15 minutes",
        3600 => "per hour",
        _ => $"per {UnitSeconds / 60} min",
    };
}
