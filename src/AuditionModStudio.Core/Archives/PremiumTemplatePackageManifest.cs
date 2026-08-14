using System.Text;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;

namespace AuditionModStudio.Core.Archives;

public sealed record PremiumTemplatePackageManifest(
    TemplateIdentity Identity,
    GameId GameId,
    ModId ModId,
    long ContentLength,
    string MediaType)
{
    public const long MaximumPackageBytes = 512L * 1024 * 1024;
    public const string PackageMediaType = "application/vnd.audition-mod-studio.template+archive";

    public bool IsValid => Identity is { IsValid: true } && GameId.IsValid && ModId.IsValid
        && ContentLength is > 0 and <= MaximumPackageBytes && MediaType == PackageMediaType;

    public byte[] GetSigningBytes() => Encoding.UTF8.GetBytes(string.Join('\n',
        "AMS-PREMIUM-TEMPLATE-V1", Identity.TemplateId.Value, Identity.Version.Value,
        Identity.Sha256.Value, Identity.CompatibleGameBuild.Value, GameId.Value, ModId.Value,
        ContentLength.ToString(System.Globalization.CultureInfo.InvariantCulture), MediaType));
}
