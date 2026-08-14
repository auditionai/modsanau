namespace Projects.Tests;

public sealed class ModuleBoundaryTests
{
    [Fact]
    public void Projects_module_references_core()
    {
        var references = typeof(AuditionModStudio.Projects.ModuleMarker)
            .Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name);

        Assert.Contains("AuditionModStudio.Core", references);
    }
}
