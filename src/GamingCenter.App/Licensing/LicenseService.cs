using System.IO;
using GamingCenter.App.Localization;
using GamingCenter.Application.Licensing;

namespace GamingCenter.App.Licensing;

/// <summary>Reads, checks and saves the license key of this PC (license.key in the data folder).</summary>
public sealed class LicenseService(string dataFolder)
{
    private string FilePath => Path.Combine(dataFolder, "license.key");

    public string MachineCode => LicenseKey.FormatMachineCode(MachineFingerprint.Code);

    /// <summary>The license currently in use, once checked.</summary>
    public LicenseInfo? Current { get; private set; }

    /// <summary>Debug builds run without a license so development is not blocked; release builds always check.</summary>
    public static bool Enforced =>
#if DEBUG
        false;
#else
        true;
#endif

    public LicenseCheck CheckSaved()
    {
        string? key = null;
        try { if (File.Exists(FilePath)) key = File.ReadAllText(FilePath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var result = Check(key);
        if (result.IsValid) Current = result.Info;
        return result;
    }

    public LicenseCheck Check(string? key) =>
        LicenseKey.Verify(key, LicensePublicKey.Value, MachineFingerprint.Code, DateOnly.FromDateTime(DateTime.Today));

    /// <summary>Checks the key and, if it is valid for this PC, saves it so the next start skips activation.</summary>
    public LicenseCheck Activate(string key)
    {
        var result = Check(key);
        if (!result.IsValid) return result;
        Directory.CreateDirectory(dataFolder);
        File.WriteAllText(FilePath, key.Trim());
        Current = result.Info;
        return result;
    }

    public static string Explain(LicenseCheck check) => check.Status switch
    {
        LicenseStatus.Valid => L.T("License activated."),
        LicenseStatus.Empty => L.T("Paste the license key you received."),
        LicenseStatus.OtherMachine => L.T("This key was made for another PC. Send the PC code shown above to get a key for this PC."),
        LicenseStatus.Expired => L.F("This license expired on {0}. Contact the seller to renew it.", check.Info?.ExpiresOn?.ToString("dd/MM/yyyy")),
        _ => L.T("This license key is not valid. Check that it was copied completely."),
    };
}
