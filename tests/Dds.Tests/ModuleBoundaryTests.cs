namespace Dds.Tests;

public sealed class ModuleBoundaryTests
{
    [Fact]
    public void Dds_module_references_core()
    {
        var references = typeof(AuditionModStudio.Dds.ModuleMarker)
            .Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name);

        Assert.Contains("AuditionModStudio.Core", references);
    }
}
