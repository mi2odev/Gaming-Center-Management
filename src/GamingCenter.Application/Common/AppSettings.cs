using GamingCenter.Domain.Billing;
using GamingCenter.Domain.Enums;

namespace GamingCenter.Application.Common;

/// <summary>All admin-editable settings. Persisted as key/value rows in the ApplicationSettings table.</summary>
public sealed class AppSettings
{
    // General
    public string CenterName { get; set; } = "mi2o Gaming Center";
    public string? LogoPath { get; set; }
    public string Address { get; set; } = "";
    public string Phone { get; set; } = "";
    public string CurrencySymbol { get; set; } = "DA";
    public string CurrencyCode { get; set; } = "DZD";

    // Billing (applies to new sessions)
    public int BillingUnitSeconds { get; set; } = 60;
    public RoundingMode Rounding { get; set; } = RoundingMode.Up;
    public int MinimumChargeMinutes { get; set; }
    public decimal MoneyRoundingStep { get; set; } = 1m;
    public decimal DefaultHourlyRate { get; set; } = 300m;
    /// <summary>Price per extra controller per hour for stations that don't set their own.</summary>
    public decimal DefaultExtraControllerRate { get; set; } = 100m;
    /// <summary>How many controllers above the included ones can be added (when the station has no maximum).</summary>
    public int DefaultMaxExtraControllers { get; set; } = 2;

    // Receipts
    public string ReceiptFooter { get; set; } = "Thank you — see you soon";
    public bool PrintReceiptByDefault { get; set; }
    public string? ReceiptPrinterName { get; set; }
    public int ReceiptWidthMm { get; set; } = 80;
    public bool ShowCustomerOnReceipt { get; set; } = true;

    // Warnings
    public int WarnBeforeEndMinutes { get; set; } = 10;
    public bool AutoEndWhenTimeExpires { get; set; }
    public int LongSessionAlertHours { get; set; } = 4;
    public bool LowStockNotifications { get; set; } = true;

    // Appearance
    public string Theme { get; set; } = "Dark";
    public string Language { get; set; } = "en";

    // Backup
    public bool AutoBackupEnabled { get; set; } = true;
    public int AutoBackupHour { get; set; } = 3;
    public string? BackupFolder { get; set; }
    public int BackupsToKeep { get; set; } = 30;
    public DateTime? LastBackupAt { get; set; }

    public BillingRules BillingRules => new(BillingUnitSeconds, Rounding, MinimumChargeMinutes, MoneyRoundingStep);

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
