namespace AuditionModStudio.Archives;

public sealed record GameRegionProfile(
    string RegionId,
    string DisplayName,
    string AcvToolCountrySelection)
{
    public static GameRegionProfile AuditionVietnam { get; } = new(
        "audition_vn",
        "AuditionVN",
        "1");
}
