using System.Management;
using Microsoft.Win32;

namespace GamingCenter.Application.Licensing;

/// <summary>
/// The PC code shown on the activation screen. It is built from the motherboard, BIOS and CPU
/// identifiers plus the Windows installation ID, so copying the program (or a cloned disk) to
/// another PC gives a different code and the license key no longer matches.
/// Reinstalling Windows or changing the motherboard also changes it: the seller then issues a new key.
/// </summary>
public static class MachineFingerprint
{
    private static string? _code;

    public static string Code => _code ??= LicenseKey.MachineCodeFrom(
    [
        Wmi("Win32_BaseBoard", "SerialNumber"),
        Wmi("Win32_BaseBoard", "Product"),
        Wmi("Win32_BIOS", "SerialNumber"),
        Wmi("Win32_ComputerSystemProduct", "UUID"),
        Wmi("Win32_Processor", "ProcessorId"),
        WindowsMachineGuid(),
    ]);

    private static string WindowsMachineGuid()
    {
        if (!OperatingSystem.IsWindows()) return "";
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string ?? "";
        }
        catch { return ""; }
    }

    private static string Wmi(string cls, string property)
    {
        if (!OperatingSystem.IsWindows()) return "";
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {cls}");
            foreach (var obj in searcher.Get())
                using (obj)
                    if (obj[property]?.ToString() is { Length: > 0 } v) return v.Trim();
        }
        catch { /* WMI unavailable: the other identifiers still make the code */ }
        return "";
    }
}
