using GamingCenter.Application.Licensing;

namespace GamingCenter.Tests;

public class LicenseKeyTests
{
    private static readonly (string Private, string Public) Keys = LicenseKey.GenerateKeyPair();
    private static readonly DateOnly Today = new(2026, 11, 18);
    private const string Pc = "ABCD-EFGH-JKMN-PQRS";

    private static string Make(string pc = Pc, DateOnly? expires = null) =>
        LicenseKey.Create(new LicenseInfo(pc, "Café Test", Today, expires), Keys.Private);

    [Fact]
    public void Key_is_valid_on_its_own_pc()
    {
        var check = LicenseKey.Verify(Make(), Keys.Public, "abcdefghjkmnpqrs", Today);
        Assert.Equal(LicenseStatus.Valid, check.Status);
        Assert.Equal("Café Test", check.Info!.Customer);
    }

    [Fact]
    public void Key_is_refused_on_another_pc() =>
        Assert.Equal(LicenseStatus.OtherMachine, LicenseKey.Verify(Make(), Keys.Public, "ABCD-EFGH-JKMN-PQRT", Today).Status);

    [Fact]
    public void Key_survives_spaces_and_line_breaks()
    {
        var key = Make();
        var mangled = " " + key[..20] + "\r\n" + key[20..60] + " " + key[60..] + "\n";
        Assert.True(LicenseKey.Verify(mangled, Keys.Public, Pc, Today).IsValid);
    }

    [Fact]
    public void Edited_payload_breaks_the_signature()
    {
        var parts = Make().Split('.');
        var other = Make("ZZZZ-EFGH-JKMN-PQRS").Split('.');
        var forged = $"{parts[0]}.{other[1]}.{parts[2]}"; // payload for another PC with this key's signature
        Assert.Equal(LicenseStatus.BadSignature, LicenseKey.Verify(forged, Keys.Public, "ZZZZ-EFGH-JKMN-PQRS", Today).Status);
    }

    [Fact]
    public void Key_from_another_seller_key_is_refused()
    {
        var otherSeller = LicenseKey.GenerateKeyPair();
        var key = LicenseKey.Create(new LicenseInfo(Pc, "X", Today, null), otherSeller.PrivateKey);
        Assert.Equal(LicenseStatus.BadSignature, LicenseKey.Verify(key, Keys.Public, Pc, Today).Status);
    }

    [Fact]
    public void Expiry_is_checked()
    {
        var key = Make(expires: Today.AddDays(10));
        Assert.True(LicenseKey.Verify(key, Keys.Public, Pc, Today.AddDays(10)).IsValid);
        Assert.Equal(LicenseStatus.Expired, LicenseKey.Verify(key, Keys.Public, Pc, Today.AddDays(11)).Status);
    }

    [Theory]
    [InlineData(null, LicenseStatus.Empty)]
    [InlineData("   ", LicenseStatus.Empty)]
    [InlineData("hello", LicenseStatus.Malformed)]
    [InlineData("MI2O1.@@@.###", LicenseStatus.Malformed)]
    public void Garbage_is_refused(string? key, LicenseStatus expected) =>
        Assert.Equal(expected, LicenseKey.Verify(key, Keys.Public, Pc, Today).Status);

    [Fact]
    public void Missing_public_key_refuses_everything() =>
        Assert.False(LicenseKey.Verify(Make(), "", Pc, Today).IsValid);

    [Fact]
    public void Machine_code_is_stable_and_16_chars()
    {
        var a = LicenseKey.MachineCodeFrom(["board-1", "bios-1", "cpu"]);
        Assert.Equal(a, LicenseKey.MachineCodeFrom(["board-1", "bios-1", "cpu"]));
        Assert.NotEqual(a, LicenseKey.MachineCodeFrom(["board-2", "bios-1", "cpu"]));
        Assert.Equal(16, a.Length);
        Assert.Equal(19, LicenseKey.FormatMachineCode(a).Length);
    }
}
