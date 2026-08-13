using System.Text;

namespace IntegrationTests;

internal enum DdsCompatibilityClassification
{
    Supported,
    Unsupported,
    Invalid,
    NotVerified,
}

internal sealed record DdsCompatibilityMatrixCase(
    string CaseId,
    string Source,
    string Scope,
    string Format,
    string Header,
    string Dimensions,
    string Mips,
    string Alpha,
    string Resource,
    DdsCompatibilityClassification MetadataRead,
    DdsCompatibilityClassification PreviewImport,
    DdsCompatibilityClassification Encode,
    DdsCompatibilityClassification MatchValidate,
    DdsCompatibilityClassification ReDecode,
    DdsCompatibilityClassification ArchiveRoundtrip,
    DdsCompatibilityClassification Overall,
    string Reason);

internal static class DdsCompatibilityMatrixEvidence
{
    private static readonly DdsCompatibilityClassification Supported =
        DdsCompatibilityClassification.Supported;
    private static readonly DdsCompatibilityClassification Unsupported =
        DdsCompatibilityClassification.Unsupported;
    private static readonly DdsCompatibilityClassification Invalid =
        DdsCompatibilityClassification.Invalid;
    private static readonly DdsCompatibilityClassification NotVerified =
        DdsCompatibilityClassification.NotVerified;

    public static IReadOnlyList<DdsCompatibilityMatrixCase> Cases { get; } = Array.AsReadOnly(
    [
        new("invalid-malformed-corpus", "Synthetic", "Parser/security boundary", "Various", "Legacy/DX10", "Invalid/oversized", "Invalid", "Unknown", "Invalid",
            Invalid, Invalid, Invalid, Invalid, Invalid, Invalid, Invalid, "Malformed headers, payloads and arithmetic profiles must fail closed"),
        new("not-verified-future-mod-types", "Private real", "Future authorized Mod Types", "Unknown", "Unknown", "Unknown", "Unknown", "Unknown", "Unknown",
            NotVerified, NotVerified, NotVerified, NotVerified, NotVerified, NotVerified, NotVerified, "Production Mod Catalog currently has no authoritative built-in Mod Type corpus"),
        new("real-015-bc1-legacy", "Private real", "Archive 015 representative", "BC1/DXT1", "Legacy", "4000x4000 (2 samples)", "1", "PossibleOneBit", "Texture2D single",
            Supported, Supported, Supported, Supported, Supported, NotVerified, NotVerified, "DDS pipeline proven; no manifest-scoped archive roundtrip for this format"),
        new("real-015-bc3-legacy", "Private real", "Archive 015 representatives", "BC3/DXT5", "Legacy", "200x200; 256x256; 6000x1801", "1; 8; 9", "Interpolated", "Texture2D single",
            Supported, Supported, Supported, Supported, Supported, NotVerified, NotVerified, "DDS pipeline proven; archive support remains slot-specific"),
        new("real-015-bgra8-legacy", "Private real", "Archive 015 representatives", "BGRA8", "Legacy", "64x64; 128x128; 512x256", "1", "Channel", "Texture2D single",
            Supported, Supported, Supported, Supported, Supported, NotVerified, NotVerified, "DDS pipeline proven; no manifest-scoped archive roundtrip for this format"),
        new("real-015-rgba8-legacy", "Private real", "Archive 015 representatives", "RGBA8", "Legacy", "64x40; 64x64; 256x256; 512x512", "1", "Channel", "Texture2D single",
            Supported, Supported, Supported, Supported, Supported, NotVerified, NotVerified, "DDS pipeline proven; no manifest-scoped archive roundtrip for this format"),
        new("real-plan97-pointer-bc3-legacy", "Private real", "PLAN 97 pointer slot", "BC3/DXT5", "Legacy", "164x128", "1", "Interpolated", "Texture2D single",
            Supported, Supported, Supported, Supported, Supported, Supported, Supported, "Full Apply, build, pack, export and re-extract evidence"),
        new("synthetic-dx10-bc1-srgb", "Synthetic", "DDS-only", "BC1", "DX10 sRGB", "7x5", "3", "Binary", "Texture2D single",
            Supported, Supported, Supported, Supported, Supported, NotVerified, NotVerified, "Full DDS pipeline proven; no authorized archive slot evidence"),
        new("synthetic-dx10-bc3-linear", "Synthetic", "DDS-only", "BC3", "DX10 linear", "9x7", "4", "Interpolated", "Texture2D single",
            Supported, Supported, Supported, Supported, Supported, NotVerified, NotVerified, "Full DDS pipeline proven; no authorized archive slot evidence"),
        new("synthetic-dx10-bgra8-linear", "Synthetic", "DDS-only", "BGRA8", "DX10 linear", "17x11", "1", "Channel", "Texture2D single",
            Supported, Supported, Supported, Supported, Supported, NotVerified, NotVerified, "Full DDS pipeline proven; no authorized archive slot evidence"),
        new("synthetic-dx10-rgba8-srgb", "Synthetic", "DDS-only", "RGBA8", "DX10 sRGB", "13x9", "4", "Channel", "Texture2D single",
            Supported, Supported, Supported, Supported, Supported, NotVerified, NotVerified, "Full DDS pipeline proven; no authorized archive slot evidence"),
        UnsupportedFormat("unsupported-format-bc2", "BC2/DXT3", "Explicit"),
        UnsupportedFormat("unsupported-format-bc4", "BC4", "None"),
        UnsupportedFormat("unsupported-format-bc5", "BC5", "None"),
        UnsupportedFormat("unsupported-format-bc6h", "BC6H", "None", "DX10"),
        UnsupportedFormat("unsupported-format-bc7", "BC7", "Channel", "DX10"),
        new("unsupported-legacy-bitmask", "Synthetic", "DDS-only", "Other uncompressed masks", "Legacy", "8x8", "1", "Varies", "Texture2D single",
            Supported, Unsupported, Unsupported, Unsupported, Unsupported, Unsupported, Unsupported, "Reader parses the header but preserves the unsupported format classification"),
        UnsupportedResource("unsupported-resource-array", "Texture2D array size 2"),
        UnsupportedResource("unsupported-resource-cubemap", "Texture2D cubemap"),
        UnsupportedResource("unsupported-resource-texture1d", "Texture1D"),
        UnsupportedResource("unsupported-resource-texture3d", "Texture3D"),
    ]);

    public static string RenderMarkdownTable()
    {
        var builder = new StringBuilder();
        builder.AppendLine("| Case ID | Source | Scope | Format | Header | Dimensions | Mips | Alpha | Resource | Metadata | Preview + Import | Encode | Match + Validate | Re-decode | Archive | Overall | Reason |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var item in Cases.OrderBy(item => item.CaseId, StringComparer.Ordinal))
        {
            builder.Append("| `").Append(item.CaseId).Append("` | ")
                .Append(item.Source).Append(" | ")
                .Append(item.Scope).Append(" | ")
                .Append(item.Format).Append(" | ")
                .Append(item.Header).Append(" | ")
                .Append(item.Dimensions).Append(" | ")
                .Append(item.Mips).Append(" | ")
                .Append(item.Alpha).Append(" | ")
                .Append(item.Resource).Append(" | ")
                .Append(Format(item.MetadataRead)).Append(" | ")
                .Append(Format(item.PreviewImport)).Append(" | ")
                .Append(Format(item.Encode)).Append(" | ")
                .Append(Format(item.MatchValidate)).Append(" | ")
                .Append(Format(item.ReDecode)).Append(" | ")
                .Append(Format(item.ArchiveRoundtrip)).Append(" | ")
                .Append(Format(item.Overall)).Append(" | ")
                .Append(item.Reason).AppendLine(" |");
        }

        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static DdsCompatibilityMatrixCase UnsupportedFormat(
        string caseId,
        string format,
        string alpha,
        string header = "Legacy/DX10") =>
        new(caseId, "Synthetic", "DDS-only boundary", format, header, "8x8", "1", alpha, "Texture2D single",
            Supported, NotVerified, Unsupported, Unsupported, NotVerified, Unsupported, Unsupported,
            "Metadata recognition is not encode, Match Original or archive support");

    private static DdsCompatibilityMatrixCase UnsupportedResource(string caseId, string resource) =>
        new(caseId, "Synthetic", "DDS-only boundary", "RGBA8", "DX10", "8x8", "1", "Channel", resource,
            Supported, Unsupported, Unsupported, Unsupported, Unsupported, Unsupported, Unsupported,
            "Editor model only supports non-cube Texture2D with array size 1");

    private static string Format(DdsCompatibilityClassification value) => value switch
    {
        DdsCompatibilityClassification.Supported => "SUPPORTED",
        DdsCompatibilityClassification.Unsupported => "UNSUPPORTED",
        DdsCompatibilityClassification.Invalid => "INVALID",
        DdsCompatibilityClassification.NotVerified => "NOT VERIFIED",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}
