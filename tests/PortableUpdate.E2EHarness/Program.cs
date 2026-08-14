using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using AuditionModStudio.Updater;

if (args is ["generate-key", var privatePath, var publicPath])
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(privatePath))!);
    await File.WriteAllTextAsync(privatePath, key.ExportPkcs8PrivateKeyPem());
    await File.WriteAllTextAsync(publicPath, key.ExportSubjectPublicKeyInfoPem());
    Console.WriteLine("TEST_SIGNING_KEY_GENERATED_NOT_PRODUCTION");
    return 0;
}

if (args is not ["run", var newRootArg, var updatesRootArg, var privateKeyArg, var publicKeyArg,
    var installRootArg, var parentPidArg]) return 2;
if (!int.TryParse(parentPidArg, out var parentPid) || parentPid <= 0) return 3;
var newRoot = Path.GetFullPath(newRootArg);
var updatesRoot = Path.GetFullPath(updatesRootArg);
var installRoot = Path.GetFullPath(installRootArg);
var files = Directory.EnumerateFiles(newRoot, "*", SearchOption.AllDirectories)
    .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray();
if (!files.Any(path => Path.GetFileName(path) == PortableUpdateProduct.PrimaryExecutable)
    || !files.Any(path => Path.GetFileName(path) == PortableUpdateProduct.UpdaterExecutable)) return 4;

using var zipOutput = new MemoryStream();
using (var zip = new ZipArchive(zipOutput, ZipArchiveMode.Create, true))
{
    foreach (var file in files)
    {
        var relative = Path.GetRelativePath(newRoot, file).Replace('\\', '/');
        var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
        await using var source = File.OpenRead(file);
        await using var destination = entry.Open();
        await source.CopyToAsync(destination);
    }
}
var packageBytes = zipOutput.ToArray();
var inventory = new List<object>();
foreach (var file in files)
{
    await using var stream = File.OpenRead(file);
    inventory.Add(new
    {
        Path = Path.GetRelativePath(newRoot, file).Replace('\\', '/'),
        Length = stream.Length,
        Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream))
    });
}
var packageUri = new Uri("https://release.example.invalid/AuditionAI-Mod-Studio-1.0.1-win-x64.zip");
var manifestUri = new Uri("https://release.example.invalid/stable.json");
var payload = JsonSerializer.SerializeToUtf8Bytes(new
{
    SchemaVersion = 3,
    Product = PortableUpdateProduct.Identity,
    Channel = "stable",
    Version = "1.0.1.0",
    MinimumSupportedVersion = "1.0.0.0",
    PublishedAt = "2026-08-14T00:00:00Z",
    UpdatePolicy = "optional",
    Package = new
    {
        Url = packageUri.AbsoluteUri,
        FileName = "AuditionAI-Mod-Studio-1.0.1-win-x64.zip",
        Size = packageBytes.LongLength,
        Sha256 = Convert.ToHexString(SHA256.HashData(packageBytes)),
        Product = PortableUpdateProduct.Identity,
        Architecture = PortableUpdateProduct.Architecture,
        Distribution = PortableUpdateProduct.Distribution,
        Inventory = inventory,
        RemoveOwnedFiles = Array.Empty<string>()
    },
    ReleaseNotes = new[] { "E2E TEST: nâng phiên bản thật" }
});
using var signer = ECDsa.Create();
signer.ImportFromPem(await File.ReadAllTextAsync(privateKeyArg));
var domain = Encoding.UTF8.GetBytes("AUDITION_APP_UPDATE_MANIFEST_V1\0");
var signature = signer.SignData(domain.Concat(payload).ToArray(), HashAlgorithmName.SHA256,
    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
var envelope = JsonSerializer.SerializeToUtf8Bytes(new
{
    SchemaVersion = 1,
    Payload = Convert.ToBase64String(payload),
    Signature = Convert.ToBase64String(signature)
});
using var verifier = new EcdsaUpdateManifestVerifier(await File.ReadAllTextAsync(publicKeyArg));
var verificationTimer = Stopwatch.StartNew();
if (!UpdateManifestCodec.TryReadEnvelope(envelope, out var verificationEnvelope)
    || !verifier.Verify(verificationEnvelope!.Payload.AsSpan(), verificationEnvelope.Signature.AsSpan())
    || !PortableUpdateManifestCodec.TryReadVerifiedPayload(verificationEnvelope.Payload.AsSpan(), out _)) return 10;
var manifestVerificationMicroseconds = verificationTimer.ElapsedTicks * 1_000_000d / Stopwatch.Frequency;
using var client = new HttpClient(new FixtureHandler(manifestUri, packageUri, envelope, packageBytes))
{
    Timeout = TimeSpan.FromSeconds(30)
};
using var coordinator = new ConfiguredPortableUpdateCoordinator(client, manifestUri, verifier,
    ["release.example.invalid"], updatesRoot, new E2EProcessLauncher());
var timer = Stopwatch.StartNew();
var check = await coordinator.CheckAsync(new Version(1, 0, 0, 0), true);
var checkMilliseconds = timer.ElapsedMilliseconds;
if (check.Availability != PortableUpdateAvailability.Available || check.Manifest is null) return 5;
timer.Restart();
var staged = await coordinator.DownloadAsync(check.Manifest);
var stageMilliseconds = timer.ElapsedMilliseconds;
if (!staged.Succeeded || staged.Update is null) return 6;
timer.Restart();
if (!coordinator.TryLaunchUpdater(staged.Update, parentPid, installRoot, out var diagnostic))
{
    Console.Error.WriteLine(diagnostic);
    return 7;
}
Console.WriteLine(JsonSerializer.Serialize(new
{
    CheckMilliseconds = checkMilliseconds,
    ManifestVerificationMicroseconds = manifestVerificationMicroseconds,
    StageMilliseconds = stageMilliseconds,
    HandoffLaunchMilliseconds = timer.ElapsedMilliseconds,
    OperationRoot = staged.Update.OperationRoot
}));
return 0;

internal sealed class FixtureHandler(
    Uri manifestUri,
    Uri packageUri,
    byte[] envelope,
    byte[] package) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bytes = request.RequestUri == manifestUri ? envelope
            : request.RequestUri == packageUri ? package
            : null;
        return Task.FromResult(new HttpResponseMessage(bytes is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, request.RequestUri),
            Content = bytes is null ? new ByteArrayContent([]) : new ByteArrayContent(bytes)
        });
    }
}

internal sealed class E2EProcessLauncher : IPortableUpdaterProcessLauncher
{
    public bool TryStart(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        return process is not null;
    }
}
