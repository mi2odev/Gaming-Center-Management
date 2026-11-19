namespace GamingCenter.Application.Licensing;

/// <summary>
/// The seller's PUBLIC key. It can only check license keys, never make them, so it is safe in the program.
/// Written by: dotnet run --project tools/LicenseTool -- init
/// The matching private key stays on the seller's PC and is never committed.
/// </summary>
internal static class LicensePublicKey
{
    // Not a const: a const would be copied as plain text into metadata, where the obfuscator cannot hide it.
    public static readonly string Value = "";
}
