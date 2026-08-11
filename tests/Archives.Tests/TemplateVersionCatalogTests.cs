using System.Globalization;
using AuditionModStudio.Archives;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Projects;

namespace Archives.Tests;

public sealed class TemplateVersionCatalogTests
{
    [Theory]
    [InlineData("template")]
    [InlineData("audition_login")]
    [InlineData("archive-015")]
    public void Template_id_accepts_stable_lowercase_ascii(string value)
    {
        Assert.Equal(value, new TemplateId(value).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Template")]
    [InlineData("template/015")]
    [InlineData("template 015")]
    public void Template_id_rejects_empty_or_noncanonical_value(string value)
    {
        Assert.Throws<ArgumentException>(() => new TemplateId(value));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.0.7")]
    [InlineData("real-fixture-v1")]
    [InlineData("build_2026")]
    public void Version_accepts_opaque_culture_independent_format(string value)
    {
        var version = new TemplateVersion(value);
        Assert.Equal(value, version.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" version")]
    [InlineData("version/1")]
    [InlineData("v 1")]
    [InlineData("v:1")]
    public void Version_rejects_empty_or_malformed_value(string value)
    {
        Assert.Throws<ArgumentException>(() => new TemplateVersion(value));
    }

    [Fact]
    public void Version_enforces_documented_length_boundary()
    {
        Assert.Equal(TemplateVersion.MaximumLength,
            new TemplateVersion(new string('v', TemplateVersion.MaximumLength)).Value.Length);
        Assert.Throws<ArgumentException>(
            () => new TemplateVersion(new string('v', TemplateVersion.MaximumLength + 1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABC")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    public void Sha256_rejects_noncanonical_integrity_value(string value)
    {
        Assert.Throws<ArgumentException>(() => new TemplateSha256(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("audition vn")]
    [InlineData("audition/vn")]
    public void Compatible_game_build_rejects_malformed_value(string value)
    {
        Assert.Throws<ArgumentException>(() => new CompatibleGameBuild(value));
    }

    [Fact]
    public void Equality_and_hash_are_ordinal_and_culture_independent()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var first = new TemplateVersion("I-1.0");
            var same = new TemplateVersion("I-1.0");
            var differentCase = new TemplateVersion("i-1.0");

            Assert.Equal(first, same);
            Assert.Equal(first.GetHashCode(), same.GetHashCode());
            Assert.NotEqual(first, differentCase);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Sha256_is_normalized_but_does_not_replace_version_identity()
    {
        var lower = new TemplateSha256(new string('a', 64));
        var upper = new TemplateSha256(new string('A', 64));

        Assert.Equal(lower, upper);
        Assert.NotEqual(new TemplateVersion("1"), new TemplateVersion("2"));
        Assert.Equal(new string('A', 64), lower.Value);
    }

    [Fact]
    public void Exact_lookup_supports_same_id_different_versions_and_explicit_current()
    {
        var v1 = Template("1", 'A');
        var v2 = Template("2", 'B');
        var result = TemplateVersionCatalog.Create([new(v2, true), new(v1, false)]);

        Assert.True(result.Succeeded);
        Assert.True(result.Catalog!.TryGetExact(new("audition_login"), new("1"), out var exact));
        Assert.Same(v1, exact);
        Assert.True(result.Catalog.TryGetCurrent(new("audition_login"), out var current));
        Assert.Same(v2, current);
    }

    [Fact]
    public void Current_version_is_explicit_and_not_inferred_from_version_order()
    {
        var v1 = Template("1", 'A');
        var v10 = Template("10", 'B');
        var catalog = Catalog(new(v1, true), new(v10, false));

        Assert.True(catalog.TryGetCurrent(new("audition_login"), out var current));
        Assert.Same(v1, current);
        Assert.Equal(TemplateResolutionStatus.CurrentVersionDiffers,
            catalog.Resolve(Snapshot("10", 'B', "audition-vn-2026")).Status);
    }

    [Fact]
    public void Duplicate_version_and_conflicting_same_version_are_rejected_atomically()
    {
        var result = TemplateVersionCatalog.Create([
            new(Template("1", 'A'), true),
            new(Template("1", 'B'), false)]);

        Assert.False(result.Succeeded);
        Assert.Null(result.Catalog);
        Assert.Contains(result.Issues, issue => issue.Reason == TemplateCatalogValidationFailureReason.DuplicateVersion);
    }

    [Fact]
    public void Each_template_id_requires_exactly_one_explicit_current_version()
    {
        var missing = TemplateVersionCatalog.Create([new(Template("1", 'A'), false)]);
        var multiple = TemplateVersionCatalog.Create([
            new(Template("1", 'A'), true),
            new(Template("2", 'B'), true)]);

        Assert.Contains(missing.Issues, issue => issue.Reason == TemplateCatalogValidationFailureReason.MissingCurrentVersion);
        Assert.Contains(multiple.Issues, issue => issue.Reason == TemplateCatalogValidationFailureReason.MultipleCurrentVersions);
    }

    [Fact]
    public void Project_exact_snapshot_resolves_without_rebinding()
    {
        var v1 = Template("1", 'A');
        var catalog = Catalog(new TemplateCatalogEntry(v1, true));
        var snapshot = Snapshot("1", 'A', "audition-vn-2026");

        var result = catalog.Resolve(snapshot);

        Assert.Equal(TemplateResolutionStatus.ExactMatch, result.Status);
        Assert.Same(v1, result.ExactTemplate);
        Assert.Same(v1, result.CurrentTemplate);
        Assert.Equal("1", snapshot.TemplateVersion?.Value);
    }

    [Fact]
    public void Different_explicit_current_is_reported_without_ordering_or_mutating_snapshot()
    {
        var v1 = Template("1", 'A');
        var v2 = Template("2", 'B');
        var catalog = Catalog(new TemplateCatalogEntry(v1, false), new TemplateCatalogEntry(v2, true));
        var snapshot = Snapshot("1", 'A', "audition-vn-2026");

        var result = catalog.Resolve(snapshot);

        Assert.Equal(TemplateResolutionStatus.CurrentVersionDiffers, result.Status);
        Assert.Same(v1, result.ExactTemplate);
        Assert.Same(v2, result.CurrentTemplate);
        Assert.Equal("1", snapshot.TemplateVersion?.Value);
        Assert.Equal(new string('A', 64), snapshot.SourceSha256.Value);
    }

    [Theory]
    [InlineData("unknown", "1", TemplateResolutionStatus.TemplateMissing)]
    [InlineData("audition_login", "missing", TemplateResolutionStatus.VersionMissing)]
    public void Missing_template_or_version_is_structured(string templateId, string version, TemplateResolutionStatus status)
    {
        var catalog = Catalog(new TemplateCatalogEntry(Template("1", 'A'), true));

        var result = catalog.Resolve(Snapshot(version, 'A', "audition-vn-2026", templateId));

        Assert.Equal(status, result.Status);
    }

    [Fact]
    public void Same_version_hash_mismatch_and_game_build_mismatch_are_distinct()
    {
        var catalog = Catalog(new TemplateCatalogEntry(Template("1", 'A'), true));

        Assert.Equal(TemplateResolutionStatus.HashMismatch,
            catalog.Resolve(Snapshot("1", 'B', "audition-vn-2026")).Status);
        Assert.Equal(TemplateResolutionStatus.IncompatibleGameBuild,
            catalog.Resolve(Snapshot("1", 'A', "audition-vn-2027")).Status);
    }

    [Fact]
    public void Legacy_snapshot_missing_version_or_build_is_invalid_and_not_defaulted()
    {
        var catalog = Catalog(new TemplateCatalogEntry(Template("1", 'A'), true));
        var missingVersion = new ArchiveTemplateReference(
            "audition_login", null, new string('A', 64), ArchiveEngineType.AcvTool5, "audition_vn", "audition-vn-2026");
        var missingBuild = new ArchiveTemplateReference(
            "audition_login", "1", new string('A', 64), ArchiveEngineType.AcvTool5, "audition_vn");

        Assert.Equal(TemplateResolutionStatus.InvalidSnapshot, catalog.Resolve(missingVersion).Status);
        Assert.Equal(TemplateResolutionStatus.InvalidSnapshot, catalog.Resolve(missingBuild).Status);
    }

    [Fact]
    public async Task Immutable_catalog_resolves_concurrently_and_deterministically()
    {
        var catalog = Catalog(new TemplateCatalogEntry(Template("1", 'A'), true));
        var snapshot = Snapshot("1", 'A', "audition-vn-2026");

        var results = await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => catalog.Resolve(snapshot))));

        Assert.All(results, result => Assert.Equal(TemplateResolutionStatus.ExactMatch, result.Status));
    }

    private static ITemplateVersionCatalog Catalog(params TemplateCatalogEntry[] entries)
    {
        var result = TemplateVersionCatalog.Create(entries);
        Assert.True(result.Succeeded, string.Join(',', result.Issues.Select(issue => issue.DiagnosticCode)));
        return result.Catalog!;
    }

    private static AuditionArchiveTemplate Template(string version, char hash) => new(
        "audition_login", "015.ab", "templates/015.ab", ArchiveEngineType.AcvTool5,
        "audition_vn", "015", version, new string(hash, 64), "audition-vn-2026");

    private static ArchiveTemplateReference Snapshot(
        string version,
        char hash,
        string build,
        string templateId = "audition_login") => new(
        templateId, version, new string(hash, 64), ArchiveEngineType.AcvTool5, "audition_vn", build);
}
