using System.Globalization;

namespace GamingCenter.Application.Common;

/// <summary>Formatting helpers shared by UI, receipts and exports.</summary>
public static class Money
{
    public static string CurrencySymbol { get; set; } = "DA";

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    /// <summary>"1,433" — no decimals when the amount is whole.</summary>
    public static string Number(decimal amount) =>
        amount == decimal.Truncate(amount)
            ? amount.ToString("#,0", Culture)
            : amount.ToString("#,0.00", Culture);

    /// <summary>"1,433 DA"</summary>
    public static string Format(decimal amount) => $"{Number(amount)} {CurrencySymbol}";

    public static string Rate(decimal hourly) => $"{Number(hourly)} {CurrencySymbol}/h";

    /// <summary>Parses user input in either "1500", "1,500" or "1500.50" form.</summary>
    public static bool TryParse(string? text, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var cleaned = text.Replace(CurrencySymbol, "", StringComparison.OrdinalIgnoreCase)
                          .Replace(" ", "").Replace(" ", "").Trim();
        if (cleaned.Count(c => c == ',') == 1 && !cleaned.Contains('.') && cleaned.Length - cleaned.IndexOf(',') <= 3)
            cleaned = cleaned.Replace(',', '.');
        else
            cleaned = cleaned.Replace(",", "");
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }
}

public static class Durations
{
    /// <summary>"01:24:35"; hours can exceed 24.</summary>
    public static string Clock(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    /// <summary>"1h 53m 42s"</summary>
    public static string Long(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return $"{(int)t.TotalHours}h {t.Minutes:00}m {t.Seconds:00}s";
    }

    /// <summary>"1h 54m"</summary>
    public static string Short(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return $"{(int)t.TotalHours}h {t.Minutes:00}m";
    }

    /// <summary>"2h", "1h 30m", "45m"</summary>
    public static string Minutes(int minutes)
    {
        int h = minutes / 60, m = minutes % 60;
        if (h == 0) return $"{m}m";
        return m == 0 ? $"{h}h" : $"{h}h {m:00}m";
    }
}
