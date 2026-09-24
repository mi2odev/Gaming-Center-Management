namespace GamingCenter.Domain.Billing;

/// <summary>
/// Hourly rate by number of controllers: the base rate covers the included controllers,
/// each extra controller adds a fixed amount per hour (e.g. 2 included at 300 DA/h, +100 DA/h each extra).
/// </summary>
public static class ControllerPricing
{
    public static decimal RateFor(decimal baseRate, int? included, decimal extraPerController, int? controllers)
    {
        if (controllers is not { } n || included is not { } inc || extraPerController <= 0) return baseRate;
        return baseRate + Math.Max(0, n - inc) * extraPerController;
    }

    /// <summary>
    /// Effective controller plan for a station: its own extra price / maximum when set,
    /// otherwise the defaults from Settings. Null when the station has no included controllers.
    /// </summary>
    public static ControllerPlan? Resolve(int? included, int? max, decimal extraPerController, decimal defaultExtra, int defaultMaxExtra)
    {
        if (included is not > 0) return null;
        decimal extra = extraPerController > 0 ? extraPerController : defaultExtra;
        int maxCount = max is { } m && m > included ? m : included.Value + Math.Max(0, defaultMaxExtra);
        if (extra <= 0 || maxCount <= included) return null;
        return new ControllerPlan(included.Value, maxCount, extra);
    }

    /// <summary>True when the station offers a choice of controller count.</summary>
    public static bool IsPriced(int? included, int? max, decimal extraPerController) =>
        included is > 0 && extraPerController > 0 && (max ?? included) > included;
}

/// <summary>Controllers included in the base price, the most allowed, and the price per extra controller per hour.</summary>
public sealed record ControllerPlan(int Included, int Max, decimal ExtraPerController)
{
    public decimal RateFor(decimal baseRate, int controllers) =>
        baseRate + Math.Max(0, controllers - Included) * ExtraPerController;
}
