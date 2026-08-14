using AuditionModStudio.Core.Dds;

namespace AuditionModStudio.Dds;

public sealed record DdsPreviewServiceOptions(
    string ApprovedToolSourcePath,
    TimeSpan Timeout,
    DdsPreviewResourcePolicy ResourcePolicy)
{
    public static DdsPreviewServiceOptions CreateProduction(string applicationBaseDirectory) => new(
        Path.GetFullPath(Path.Combine(
            applicationBaseDirectory,
            "Tools",
            "DirectXTex",
            DirectXTexEvaluationToolCatalog.May2026X64.FileName)),
        TimeSpan.FromMinutes(2),
        DdsPreviewResourcePolicy.Default);
}
