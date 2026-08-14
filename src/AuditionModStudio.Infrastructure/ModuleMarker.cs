namespace AuditionModStudio.Infrastructure;

/// <summary>
/// Identifies the Infrastructure assembly for composition and tests.
/// </summary>
public static class ModuleMarker
{
    public static Type CoreModule => typeof(Core.ModuleMarker);
}
