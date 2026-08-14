namespace Core.Tests;

public sealed class CoreDependencyTests
{
    [Theory]
    [InlineData("AuditionModStudio.App")]
    [InlineData("AuditionModStudio.Cloud")]
    [InlineData("Microsoft.UI.Xaml")]
    public void Core_does_not_reference_ui_or_cloud_implementation(string forbiddenAssembly)
    {
        var references = typeof(AuditionModStudio.Core.ModuleMarker)
            .Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name);

        Assert.DoesNotContain(forbiddenAssembly, references);
    }
}
