using System.Globalization;
using System.Text;
using GamingCenter.Application.Licensing;

// mi2o license tool — for the SELLER only. Do not give it (or the private key) to customers.
//
//   dotnet run --project tools/LicenseTool -- init
//   dotnet run --project tools/LicenseTool -- issue ABCD-EFGH-JKMN-PQRS "Café Djezzy Oran"
//   dotnet run --project tools/LicenseTool -- issue ABCD-EFGH-JKMN-PQRS "Café X" --expires 2027-12-31
//   dotnet run --project tools/LicenseTool -- list
//
// The private key lives in %USERPROFILE%\mi2o-license\private.key (or MI2O_LICENSE_DIR).
// BACK IT UP (USB key, cloud drive): without it you cannot make keys for the copies you already sold.

var dir = Environment.GetEnvironmentVariable("MI2O_LICENSE_DIR") is { Length: > 0 } d
    ? d
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "mi2o-license");
var privatePath = Path.Combine(dir, "private.key");
var publicPath = Path.Combine(dir, "public.key");
var logPath = Path.Combine(dir, "licenses.csv");

try
{
    return args.FirstOrDefault()?.ToLowerInvariant() switch
    {
        "init" => Init(args.Contains("--force")),
        "issue" => Issue(args.Skip(1).ToArray()),
        "verify" => VerifyKey(args.Skip(1).ToArray()),
        "list" => List(),
        _ => Usage(),
    };
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine("Error: " + ex.Message);
    Console.ResetColor();
    return 1;
}

int Usage()
{
    Console.WriteLine("""
        mi2o license tool (seller only)

          init                               Create your key pair and write the public key into the app.
          issue <PC-CODE> "<customer>" [--expires yyyy-MM-dd]
                                             Make a license key for one PC.
          verify <KEY> <PC-CODE>             Check a key.
          list                               Show every key you issued.
        """);
    return 2;
}

int Init(bool force)
{
    if (File.Exists(privatePath) && !force)
    {
        Console.WriteLine($"A private key already exists: {privatePath}");
        Console.WriteLine("Keeping it. (Replacing it would make every key you already sold stop working;");
        Console.WriteLine(" use --force only if you really want that.)");
        WritePublicKeyIntoApp(File.ReadAllText(publicPath).Trim());
        return 0;
    }

    var (priv, pub) = LicenseKey.GenerateKeyPair();
    Directory.CreateDirectory(dir);
    File.WriteAllText(privatePath, priv);
    File.WriteAllText(publicPath, pub);
    Console.WriteLine($"Private key created: {privatePath}");
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("BACK UP this folder now and never share it or commit it to git.");
    Console.ResetColor();
    WritePublicKeyIntoApp(pub);
    return 0;
}

void WritePublicKeyIntoApp(string pub)
{
    var root = FindRepoRoot();
    if (root is null)
    {
        Console.WriteLine("Repository not found. Put this public key in src/GamingCenter.App/Licensing/LicensePublicKey.cs:");
        Console.WriteLine(pub);
        return;
    }
    var file = Path.Combine(root, "src", "GamingCenter.App", "Licensing", "LicensePublicKey.cs");
    var text = File.ReadAllText(file);
    var start = text.IndexOf("Value = \"", StringComparison.Ordinal) + "Value = \"".Length;
    var end = text.IndexOf('"', start);
    if (text[start..end] == pub) { Console.WriteLine("The app already has this public key."); return; }
    File.WriteAllText(file, text[..start] + pub + text[end..]);
    Console.WriteLine($"Public key written into {file}");
    Console.WriteLine("Rebuild the installer so the app checks keys with it.");
}

int Issue(string[] a)
{
    var positional = a.Where((x, i) => !x.StartsWith("--") && (i == 0 || !a[i - 1].StartsWith("--"))).ToArray();
    if (positional.Length < 2) return Usage();
    if (!File.Exists(privatePath)) throw new InvalidOperationException($"No private key in {dir}. Run 'init' first.");

    var machine = LicenseKey.NormalizeMachineCode(positional[0]);
    if (machine.Length != 16) throw new ArgumentException($"A PC code has 16 letters/digits (e.g. ABCD-EFGH-JKMN-PQRS); got '{positional[0]}'.");
    var customer = positional[1];

    DateOnly? expires = null;
    var ei = Array.IndexOf(a, "--expires");
    if (ei >= 0 && ei + 1 < a.Length) expires = DateOnly.ParseExact(a[ei + 1], "yyyy-MM-dd", CultureInfo.InvariantCulture);

    var info = new LicenseInfo(machine, customer, DateOnly.FromDateTime(DateTime.Today), expires);
    var key = LicenseKey.Create(info, File.ReadAllText(privatePath).Trim());

    // Self-check with the public key before handing it out.
    var check = LicenseKey.Verify(key, File.ReadAllText(publicPath).Trim(), machine, info.IssuedOn);
    if (!check.IsValid) throw new InvalidOperationException("The new key failed its own check: " + check.Status);

    var isNew = !File.Exists(logPath);
    File.AppendAllText(logPath, (isNew ? "issued,pc_code,customer,expires,key\n" : "") +
        $"{info.IssuedOn:yyyy-MM-dd},{LicenseKey.FormatMachineCode(machine)},\"{customer.Replace("\"", "'")}\",{expires:yyyy-MM-dd},{key}\n", Encoding.UTF8);

    Console.WriteLine($"Customer : {customer}");
    Console.WriteLine($"PC code  : {LicenseKey.FormatMachineCode(machine)}");
    Console.WriteLine($"Expires  : {(expires is null ? "never" : expires.Value.ToString("yyyy-MM-dd"))}");
    Console.WriteLine();
    Console.WriteLine("License key (send this to the customer):");
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine(key);
    Console.ResetColor();
    return 0;
}

int VerifyKey(string[] a)
{
    if (a.Length < 2) return Usage();
    var check = LicenseKey.Verify(a[0], File.ReadAllText(publicPath).Trim(), a[1], DateOnly.FromDateTime(DateTime.Today));
    Console.WriteLine($"{check.Status}  {check.Info}");
    return check.IsValid ? 0 : 1;
}

int List()
{
    Console.WriteLine(File.Exists(logPath) ? File.ReadAllText(logPath) : "No key issued yet.");
    return 0;
}

static string? FindRepoRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GamingCenter.sln"))) return d.FullName;
    return null;
}
