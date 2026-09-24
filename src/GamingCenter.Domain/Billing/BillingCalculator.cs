using GamingCenter.Domain.Enums;

namespace GamingCenter.Domain.Billing;

/// <summary>Pure money maths. All amounts are <see cref="decimal"/>; no floating point is used.</summary>
public static class BillingCalculator
{
    /// <summary>Seconds that will actually be charged after unit rounding and minimum charge.</summary>
    public static long BillableSeconds(TimeSpan played, BillingRules rules)
    {
        long seconds = (long)Math.Floor(played.TotalSeconds);
        if (seconds <= 0) return 0;

        long minimum = rules.MinimumChargeMinutes * 60L;
        if (seconds < minimum) seconds = minimum;

        if (rules.UnitSeconds <= 1) return seconds;

        long unit = rules.UnitSeconds;
        long whole = seconds / unit;
        long rest = seconds % unit;
        long units = rules.Rounding switch
        {
            RoundingMode.Up => rest > 0 ? whole + 1 : whole,
            RoundingMode.Down => whole,
            _ => rest * 2 >= unit ? whole + 1 : whole,
        };
        return units * unit;
    }

    /// <summary>Price of <paramref name="played"/> at <paramref name="hourlyRate"/> under <paramref name="rules"/>.</summary>
    public static decimal Cost(TimeSpan played, decimal hourlyRate, BillingRules rules)
    {
        if (hourlyRate <= 0) return 0m;
        long billable = BillableSeconds(played, rules);
        decimal raw = hourlyRate * billable / 3600m;
        return RoundMoney(raw, rules.MoneyRoundingStep);
    }

    public static decimal RoundMoney(decimal amount, decimal step)
    {
        if (step <= 0) return Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        return Math.Round(amount / step, 0, MidpointRounding.AwayFromZero) * step;
    }

    /// <summary>How long a budget lasts at a rate (exact, before billing units).</summary>
    public static TimeSpan TimeForBudget(decimal budget, decimal hourlyRate)
    {
        if (hourlyRate <= 0 || budget <= 0) return TimeSpan.Zero;
        decimal seconds = Math.Floor(budget * 3600m / hourlyRate);
        return TimeSpan.FromSeconds((double)seconds);
    }

    /// <summary>Price shown for one billing unit, e.g. "15 minutes = 100 DA".</summary>
    public static decimal PricePerUnit(decimal hourlyRate, BillingRules rules)
    {
        int unit = rules.UnitSeconds <= 1 ? 60 : rules.UnitSeconds;
        return hourlyRate * unit / 3600m;
    }
}
