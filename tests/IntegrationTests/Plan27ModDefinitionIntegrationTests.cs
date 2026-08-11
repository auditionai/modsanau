using AuditionModStudio.Archives;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Mods;

namespace IntegrationTests;

public sealed class Plan27ModDefinitionIntegrationTests
{
    [Fact]
    public void Game_to_mod_to_archive_mapping_preserves_trusted_metadata()
    {
        var gameCatalog = GameCatalog.CreateBuiltIn();
        var regionProfiles = new GameRegionProfileCatalog();
        var definition = CreateDefinition("login_screen", "015.ab", "015");
        var result = ModCatalog.Create([definition], gameCatalog, regionProfiles);

        Assert.True(result.Succeeded);
        Assert.True(result.Catalog!.TryGetMod(
            new GameId("audition"),
            new ModId("login_screen"),
            out var resolved));
        Assert.Equal("audition-login-template", resolved!.ArchiveTemplate.ArchiveId);
        Assert.Equal("2026.1", resolved.ArchiveTemplate.TemplateVersion);
        Assert.Equal("015.ab", resolved.ArchiveTemplate.FileName);
        Assert.Equal("015", resolved.ArchiveTemplate.ExpectedExtractFolderName);
        Assert.Same(ArchiveEngineType.AcvTool5, resolved.ArchiveTemplate.EngineType);
        Assert.Equal("audition_vn", resolved.ArchiveTemplate.RegionProfileId);
        Assert.Equal(ModKeydatStrategy.ReuseOrGenerate, resolved.KeydatStrategy);
    }

    [Fact]
    public void Region_profile_resolves_country_selection_outside_mod_contract()
    {
        var definition = CreateDefinition("login_screen", "015.ab", "015");
        var regionProfiles = new GameRegionProfileCatalog();

        var found = regionProfiles.TryResolve(
            definition.ArchiveTemplate.RegionProfileId,
            out var profile);

        Assert.True(found);
        Assert.Equal("AuditionVN", profile.DisplayName);
        Assert.Equal("1", profile.AcvToolCountrySelection);
        Assert.DoesNotContain(
            typeof(ModDefinition).GetProperties(),
            property => property.PropertyType == typeof(GameRegionProfile));
    }

    [Fact]
    public void Archive_engine_mapping_is_independent_of_archive_extension()
    {
        var gameCatalog = GameCatalog.CreateBuiltIn();
        var regionProfiles = new GameRegionProfileCatalog();
        var result = ModCatalog.Create(
        [
            CreateDefinition("ab_mod", "015.ab", "015"),
            CreateDefinition("acv_mod", "021.acv", "021")
        ],
        gameCatalog,
        regionProfiles);

        Assert.True(result.Succeeded);
        Assert.All(
            result.Catalog!.GetMods(new GameId("audition")),
            definition => Assert.Same(
                ArchiveEngineType.AcvTool5,
                definition.ArchiveTemplate.EngineType));
    }

    private static ModDefinition CreateDefinition(
        string modId,
        string fileName,
        string extractFolder) => new(
            new ModId(modId),
            new GameId("audition"),
            $"Semantic {modId}",
            new ModCategory("interface"),
            new ModRelativePath($"Mods/Covers/{modId}.png"),
            "Synthetic integration definition for PLAN 27 mapping validation.",
            new AuditionArchiveTemplate(
                "audition-login-template",
                fileName,
                $"Templates/{fileName}",
                ArchiveEngineType.AcvTool5,
                GameRegionProfile.AuditionVietnam.RegionId,
                extractFolder,
                "2026.1"),
            ModKeydatStrategy.ReuseOrGenerate,
            new ModRelativePath($"Data/{fileName}"),
            "Synthetic compatibility metadata.");
}
