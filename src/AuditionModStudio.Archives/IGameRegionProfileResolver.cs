namespace AuditionModStudio.Archives;

public interface IGameRegionProfileResolver
{
    bool TryResolve(string regionProfileId, out GameRegionProfile profile);
}
