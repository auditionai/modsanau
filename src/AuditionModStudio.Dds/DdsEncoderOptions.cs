using AuditionModStudio.Core.Dds;

namespace AuditionModStudio.Dds;

public sealed record DdsEncoderOptions(
    string ApprovedToolSourcePath,
    TimeSpan Timeout,
    DdsPreviewResourcePolicy ResourcePolicy)
{
    public static DdsEncoderOptions CreateProduction(string applicationBaseDirectory) => new(
        Path.GetFullPath(Path.Combine(
            applicationBaseDirectory,
            "Tools",
            "DirectXTex",
            DirectXTexEvaluationToolCatalog.May2026X64.FileName)),
        TimeSpan.FromMinutes(3),
        DdsPreviewResourcePolicy.Default);
}
