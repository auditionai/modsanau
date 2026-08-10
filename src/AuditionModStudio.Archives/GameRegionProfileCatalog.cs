namespace AuditionModStudio.Archives;

public sealed class GameRegionProfileCatalog : IGameRegionProfileResolver
{
    private static readonly IReadOnlyDictionary<string, GameRegionProfile> Profiles =
        new Dictionary<string, GameRegionProfile>(StringComparer.OrdinalIgnoreCase)
        {
            [GameRegionProfile.AuditionVietnam.RegionId] = GameRegionProfile.AuditionVietnam,
        };

    public bool TryResolve(string regionProfileId, out GameRegionProfile profile) =>
        Profiles.TryGetValue(regionProfileId, out profile!);
}
