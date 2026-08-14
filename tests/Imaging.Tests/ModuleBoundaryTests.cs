namespace Imaging.Tests;

public sealed class ModuleBoundaryTests
{
    [Fact]
    public void Imaging_module_references_core()
    {
        var references = typeof(AuditionModStudio.Imaging.ModuleMarker)
            .Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name);

        Assert.Contains("AuditionModStudio.Core", references);
    }
}
