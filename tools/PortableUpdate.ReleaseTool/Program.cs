using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuditionModStudio.Updater;

var options = Parse(args);
if (options is null) return 2;
var packageRoot = Path.GetFullPath(options["--package-root"]);
var outputRoot = Path.GetFullPath(options["--output"]);
var privateKeyPath = Path.GetFullPath(options["--private-key"]);
if (!Directory.Exists(packageRoot) || !File.Exists(privateKeyPath)
    || !Version.TryParse(options["--version"], out var version) || version.Build < 0
    || !Version.TryParse(options["--minimum-version"], out var minimum) || minimum.Build < 0
    || minimum.CompareTo(version) > 0
    || !Uri.TryCreate(options["--package-uri"], UriKind.Absolute, out var packageUri)
    || packageUri.Scheme != Uri.UriSchemeHttps || packageUri.Port != 443)
    return 3;
var notes = File.ReadAllLines(options["--release-notes"], Encoding.UTF8)
    .Where(static line => !string.IsNullOrWhiteSpace(line)).Select(static line => line.Trim()).ToArray();
if (notes.Length > PortableUpdateManifestCodec.MaximumReleaseNotes || notes.Any(static note => note.Length > 240))
    return 4;

Directory.CreateDirectory(outputRoot);
var packageName = $"AuditionAI-Mod-Studio-{version}-win-x64.zip";
if (!packageUri.AbsolutePath.EndsWith('/' + packageName, StringComparison.Ordinal)) return 5;
var packagePath = Path.Combine(outputRoot, packageName);
var files = Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
    .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray();
var inventory = new List<object>(files.Length);
using (var output = new FileStream(packagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
{
    foreach (var file in files)
    {
        var relative = Path.GetRelativePath(packageRoot, file).Replace('\\', '/');
        if (!PortableUpdateManifestCodec.ValidRelativePath(relative)) return 6;
        await using var input = File.OpenRead(file);
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(input));
        inventory.Add(new { Path = relative, Length = input.Length, Sha256 = sha256 });
        input.Position = 0;
        var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        await input.CopyToAsync(entryStream);
    }
}
if (!inventory.Any(entry => JsonSerializer.Serialize(entry).Contains(PortableUpdateProduct.PrimaryExecutable,
        StringComparison.Ordinal))
    || !inventory.Any(entry => JsonSerializer.Serialize(entry).Contains(PortableUpdateProduct.UpdaterExecutable,
        StringComparison.Ordinal))) return 7;
await using var packageStream = File.OpenRead(packagePath);
var packageHash = Convert.ToHexString(await SHA256.HashDataAsync(packageStream));
var payload = JsonSerializer.SerializeToUtf8Bytes(new
{
    SchemaVersion = PortableUpdateManifestCodec.SchemaVersion,
    Product = PortableUpdateProduct.Identity,
    Channel = "stable",
    Version = version.ToString(),
    MinimumSupportedVersion = minimum.ToString(),
    PublishedAt = DateTimeOffset.UtcNow.ToString("O"),
    UpdatePolicy = options["--policy"],
    Package = new
    {
        Url = packageUri.AbsoluteUri,
        FileName = packageName,
        Size = packageStream.Length,
        Sha256 = packageHash,
        Product = PortableUpdateProduct.Identity,
        Architecture = PortableUpdateProduct.Architecture,
        Distribution = PortableUpdateProduct.Distribution,
        Inventory = inventory,
        RemoveOwnedFiles = Array.Empty<string>()
    },
    ReleaseNotes = notes
});
using var signer = ECDsa.Create();
signer.ImportFromPem(await File.ReadAllTextAsync(privateKeyPath));
if (signer.KeySize != 256) return 8;
var domain = Encoding.UTF8.GetBytes("AUDITION_APP_UPDATE_MANIFEST_V1\0");
var signature = signer.SignData(domain.Concat(payload).ToArray(), HashAlgorithmName.SHA256,
    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
var envelope = JsonSerializer.SerializeToUtf8Bytes(new
{
    SchemaVersion = 1,
    Payload = Convert.ToBase64String(payload),
    Signature = Convert.ToBase64String(signature)
}, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllBytesAsync(Path.Combine(outputRoot, "stable.manifest.signed.json"), envelope);
await File.WriteAllBytesAsync(Path.Combine(outputRoot, "portable-update-payload.json"), payload);
var report = JsonSerializer.Serialize(new
{
    Status = "CREATED_NOT_PUBLISHED",
    Version = version.ToString(),
    FileName = packageName,
    Size = packageStream.Length,
    Sha256 = packageHash,
    InventoryCount = inventory.Count,
    Signing = "ES256_PRIVATE_KEY_EXTERNAL"
}, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(outputRoot, "portable-update-release-report.json"), report);
Console.WriteLine(report);
return 0;

static Dictionary<string, string>? Parse(string[] args)
{
    if (args.Length != 16) return null;
    var allowed = new[] { "--package-root", "--version", "--minimum-version", "--package-uri",
        "--private-key", "--release-notes", "--policy", "--output" };
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < args.Length; index += 2)
        if (!allowed.Contains(args[index], StringComparer.Ordinal) || !values.TryAdd(args[index], args[index + 1]))
            return null;
    if (values["--policy"] is not ("optional" or "required")) return null;
    return values;
}
