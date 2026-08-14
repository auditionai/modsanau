namespace IntegrationTests;

public sealed class SolutionBoundaryTests
{
    [Fact]
    public void Foundation_modules_have_unique_assembly_names()
    {
        var names = new[]
        {
            typeof(AuditionModStudio.Archives.ModuleMarker).Assembly.GetName().Name!,
            typeof(AuditionModStudio.Core.ModuleMarker).Assembly.GetName().Name!,
            typeof(AuditionModStudio.Dds.ModuleMarker).Assembly.GetName().Name!,
            typeof(AuditionModStudio.Imaging.ModuleMarker).Assembly.GetName().Name!,
            typeof(AuditionModStudio.Mods.ModuleMarker).Assembly.GetName().Name!,
            typeof(AuditionModStudio.Projects.ModuleMarker).Assembly.GetName().Name!,
            typeof(AuditionModStudio.Security.ModuleMarker).Assembly.GetName().Name!,
        };

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }
}
