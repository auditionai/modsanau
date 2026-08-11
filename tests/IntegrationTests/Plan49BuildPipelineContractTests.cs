namespace IntegrationTests;

public sealed class Plan49BuildPipelineContractTests
{
    [Fact]
    public void Build_pipeline_is_registered_with_production_archive_engine_and_no_direct_process_launch()
    {
        var root = FindRepositoryRoot();
        var bootstrap = File.ReadAllText(Path.Combine(
            root, "src", "AuditionModStudio.App", "Bootstrap", "ApplicationBootstrapper.cs"));
        var service = File.ReadAllText(Path.Combine(
            root, "src", "AuditionModStudio.Projects", "ProjectBuildService.cs"));

        Assert.Contains("IProjectBuildService, ProjectBuildService", bootstrap, StringComparison.Ordinal);
        Assert.Contains("IArchiveEngine, AcvTool5ArchiveEngine", bootstrap, StringComparison.Ordinal);
        Assert.Contains("IAuditionArchiveService archiveService", service, StringComparison.Ordinal);
        Assert.Contains("archiveService.PackAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", service, StringComparison.Ordinal);
        Assert.DoesNotContain("-ca", service, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd.exe", service, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_contract_exposes_exact_required_terminal_and_progress_phases()
    {
        var root = FindRepositoryRoot();
        var contract = File.ReadAllText(Path.Combine(
            root, "src", "AuditionModStudio.Core", "Projects", "IProjectBuildService.cs"));

        foreach (var phase in new[]
                 {
                     "Preparing", "Validating", "Packing", "Verifying",
                     "Completed", "Failed", "Cancelled"
                 })
        {
            Assert.Contains(phase, contract, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
