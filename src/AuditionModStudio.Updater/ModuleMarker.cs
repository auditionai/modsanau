namespace AuditionModStudio.Updater;

/// <summary>
/// Identifies the Updater assembly for composition and tests.
/// </summary>
public static class ModuleMarker
{
    public static Type CoreModule => typeof(Core.ModuleMarker);
}
