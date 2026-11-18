namespace GamingCenter.App.Licensing;

/// <summary>
/// The seller's PUBLIC key. It can only check license keys, never make them, so it is safe in the program.
/// Written by: dotnet run --project tools/LicenseTool -- init
/// The matching private key stays on the seller's PC and is never committed.
/// </summary>
internal static class LicensePublicKey
{
    public const string Value = "";
}
