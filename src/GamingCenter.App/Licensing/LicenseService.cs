using GamingCenter.App.Localization;
using GamingCenter.Application.Licensing;

namespace GamingCenter.App.Licensing;

/// <summary>UI side of the license: the PC code, activation and messages. The checks live in <see cref="LicenseRuntime"/>.</summary>
public sealed class LicenseService
{
    public LicenseService(string dataFolder) => LicenseRuntime.UseDataFolder(dataFolder);

    public string MachineCode => LicenseKey.FormatMachineCode(MachineFingerprint.Code);

    public LicenseInfo? Current => LicenseRuntime.Current;

    public static bool Enforced => LicenseRuntime.Enforced;

    public LicenseCheck CheckSaved() => LicenseRuntime.Check(fresh: true);

    public LicenseCheck Activate(string key) => LicenseRuntime.Activate(key);

    /// <summary>"Licensed to Café X" for the login screen, settings and receipts; empty when not licensed.</summary>
    public static string LicensedToText => LicenseRuntime.Current is { } info ? L.F("Licensed to {0}", info.Customer) : "";

    public static string Explain(LicenseCheck check) => check.Status switch
    {
        LicenseStatus.Valid => L.T("License activated."),
        LicenseStatus.Empty => L.T("Paste the license key you received."),
        LicenseStatus.OtherMachine => L.T("This key was made for another PC. Send the PC code shown above to get a key for this PC."),
        LicenseStatus.Expired => L.F("This license expired on {0}. Contact the seller to renew it.", check.Info?.ExpiresOn?.ToString("dd/MM/yyyy")),
        _ => L.T("This license key is not valid. Check that it was copied completely."),
    };
}
