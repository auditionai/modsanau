using AuditionModStudio.Archives;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Mods;

namespace IntegrationTests;

public sealed class Plan28TextureManifestIntegrationTests
{
    [Fact]
    public void Game_to_mod_to_manifest_to_texture_slot_preserves_explicit_identity()
    {
        var mods = ModCatalog.Create([CreateMod()], GameCatalog.CreateBuiltIn(), new GameRegionProfileCatalog()).Catalog!;
        var manifest = TextureManifest.Create(
            new("audition"), new("login_screen"),
            [new TextureSlot(new("logo_main"), new("Texture/Login/logo.dds"), "Logo đăng nhập",
                new("branding"), "Synthetic metadata", ["logo", "login"], true, true, new("alpha_aware"))]).Manifest!;

        var result = TextureManifestCatalog.Create([manifest], mods);
        var resolved = result.Catalog!.Resolve(new("audition"), new("login_screen"), new("texture/login/LOGO.DDS"));

        Assert.True(result.Succeeded);
        Assert.False(resolved.UsedFallback);
        Assert.Equal("logo_main", resolved.Slot!.Id.Value);
        Assert.Equal("Texture/Login/logo.dds", resolved.Slot.RelativePath.Value);
        Assert.Equal("Logo đăng nhập", resolved.DisplayName);
    }

    [Fact]
    public void Missing_manifest_falls_back_without_filesystem_dds_or_archive_work()
    {
        var mods = ModCatalog.Create([CreateMod()], GameCatalog.CreateBuiltIn(), new GameRegionProfileCatalog()).Catalog!;
        var catalog = TextureManifestCatalog.Create([], mods).Catalog!;

        var resolved = catalog.Resolve(new("audition"), new("login_screen"), new("Texture/Unknown/raw.dds"));

        Assert.True(resolved.UsedFallback);
        Assert.Equal("raw.dds", resolved.DisplayName);
        Assert.Null(resolved.Slot);
    }

    private static ModDefinition CreateMod() => new(
        new("login_screen"), new("audition"), "Login screen", new("interface"),
        new("Mods/Covers/login.png"), "Synthetic PLAN 28 integration definition.",
        new("audition-login-template", "015.ab", "Templates/015.ab", ArchiveEngineType.AcvTool5,
            GameRegionProfile.AuditionVietnam.RegionId, "015", "2026.1"),
        ModKeydatStrategy.ReuseOrGenerate, new("Data/015.ab"), "Synthetic compatibility metadata.");
}
