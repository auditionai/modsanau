namespace Security.Tests;

public sealed class ModuleBoundaryTests
{
    [Fact]
    public void Security_module_references_core()
    {
        var references = typeof(AuditionModStudio.Security.ModuleMarker)
            .Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name);

        Assert.Contains("AuditionModStudio.Core", references);
    }
}
