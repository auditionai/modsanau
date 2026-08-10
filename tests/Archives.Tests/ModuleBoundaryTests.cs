namespace Archives.Tests;

public sealed class ModuleBoundaryTests
{
    [Fact]
    public void Archives_module_references_core()
    {
        var references = typeof(AuditionModStudio.Archives.ModuleMarker)
            .Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name);

        Assert.Contains("AuditionModStudio.Core", references);
    }
}
