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

    /// <summary>True when the station offers a choice of controller count.</summary>
    public static bool IsPriced(int? included, int? max, decimal extraPerController) =>
        included is > 0 && extraPerController > 0 && (max ?? included) > included;
}
