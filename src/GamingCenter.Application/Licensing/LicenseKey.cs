using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GamingCenter.Application.Licensing;

/// <summary>What a license key says: who bought the app, for which PC, and until when.</summary>
public sealed record LicenseInfo(string MachineCode, string Customer, DateOnly IssuedOn, DateOnly? ExpiresOn)
{
    public bool IsExpired(DateOnly today) => ExpiresOn is { } end && today > end;
}

public enum LicenseStatus { Valid, Empty, Malformed, BadSignature, OtherMachine, Expired }

public sealed record LicenseCheck(LicenseStatus Status, LicenseInfo? Info)
{
    public bool IsValid => Status == LicenseStatus.Valid;
}

/// <summary>
/// Offline license keys locked to one PC.
/// A key is "MI2O1.{payload}.{signature}" (base64url). The payload holds the PC code, customer name
/// and dates; the signature is ECDSA P-256 made with the seller's private key.
/// The app only carries the public key: it can check a key but never make one, so a key can't be
/// forged, and a key made for one PC code is refused on any other PC.
/// </summary>
public static class LicenseKey
{
    private const string Prefix = "MI2O1";
    private const char Sep = '|';

    /// <summary>Makes a key. Needs the seller's private key (PKCS#8, base64), so it only runs in the license tool.</summary>
    public static string Create(LicenseInfo info, string privateKeyPkcs8Base64)
    {
        var machine = NormalizeMachineCode(info.MachineCode);
        if (machine.Length == 0) throw new ArgumentException("The PC code is empty.");
        var customer = info.Customer.Replace(Sep, ' ').Trim();
        if (customer.Length == 0) throw new ArgumentException("The customer name is empty.");

        var payload = string.Join(Sep, "1", machine, customer,
            info.IssuedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            info.ExpiresOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "");
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyPkcs8Base64), out _);
        var signature = ecdsa.SignData(payloadBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{Prefix}.{B64Url(payloadBytes)}.{B64Url(signature)}";
    }

    /// <summary>Checks a key against the app's public key and this PC's code.</summary>
    public static LicenseCheck Verify(string? key, string publicKeySpkiBase64, string machineCode, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(key)) return new(LicenseStatus.Empty, null);

        // Keys travel through WhatsApp / e-mail: ignore spaces and line breaks added on the way.
        var compact = new string(key.Where(c => !char.IsWhiteSpace(c)).ToArray());
        var parts = compact.Split('.');
        if (parts.Length != 3 || parts[0] != Prefix) return new(LicenseStatus.Malformed, null);

        byte[] payloadBytes, signature;
        try { payloadBytes = FromB64Url(parts[1]); signature = FromB64Url(parts[2]); }
        catch (FormatException) { return new(LicenseStatus.Malformed, null); }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64), out _);
            if (!ecdsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return new(LicenseStatus.BadSignature, null);
        }
        catch (CryptographicException) { return new(LicenseStatus.BadSignature, null); }
        catch (FormatException) { return new(LicenseStatus.BadSignature, null); }

        var info = ParsePayload(Encoding.UTF8.GetString(payloadBytes));
        if (info is null) return new(LicenseStatus.Malformed, null);
        if (info.MachineCode != NormalizeMachineCode(machineCode)) return new(LicenseStatus.OtherMachine, info);
        if (info.IsExpired(today)) return new(LicenseStatus.Expired, info);
        return new(LicenseStatus.Valid, info);
    }

    /// <summary>"abcd efgh-ijkl" → "ABCDEFGHIJKL", so a code typed by hand still matches.</summary>
    public static string NormalizeMachineCode(string code) =>
        new(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Groups a normalized code for display: ABCD-EFGH-IJKL-MNOP.</summary>
    public static string FormatMachineCode(string code)
    {
        var n = NormalizeMachineCode(code);
        return string.Join('-', Enumerable.Range(0, (n.Length + 3) / 4).Select(i => n.Substring(i * 4, Math.Min(4, n.Length - i * 4))));
    }

    /// <summary>Turns hardware identifiers into a short, stable PC code (16 characters, Crockford base32).</summary>
    public static string MachineCodeFrom(IEnumerable<string> hardwareIds)
    {
        var joined = string.Join("\n", hardwareIds.Select(s => (s ?? "").Trim().ToUpperInvariant()));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("mi2o-pc|" + joined));
        return Base32(hash.AsSpan(0, 10)); // 80 bits → 16 characters
    }

    /// <summary>Creates a new seller key pair: (private PKCS#8 base64, public SPKI base64).</summary>
    public static (string PrivateKey, string PublicKey) GenerateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()), Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
    }

    private static LicenseInfo? ParsePayload(string payload)
    {
        var f = payload.Split(Sep);
        if (f.Length != 5 || f[0] != "1") return null;
        if (!DateOnly.TryParseExact(f[3], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var issued)) return null;
        DateOnly? expires = null;
        if (f[4].Length > 0)
        {
            if (!DateOnly.TryParseExact(f[4], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var e)) return null;
            expires = e;
        }
        return new LicenseInfo(f[1], f[2], issued, expires);
    }

    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static string Base32(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(Crockford[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Crockford[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static string B64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromB64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }
}
