using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.Archives;

public sealed class TemplateVersionCatalog : ITemplateVersionCatalog
{
    private readonly ImmutableDictionary<(TemplateId Id, TemplateVersion Version), AuditionArchiveTemplate> _exact;
    private readonly ImmutableDictionary<TemplateId, AuditionArchiveTemplate> _current;

    private TemplateVersionCatalog(IEnumerable<TemplateCatalogEntry> entries)
    {
        var materialized = entries.ToImmutableArray();
        _exact = materialized.ToImmutableDictionary(
            entry => (entry.Template.TemplateId, entry.Template.TemplateVersion!.Value),
            entry => entry.Template);
        _current = materialized
            .Where(entry => entry.IsCurrent)
            .ToImmutableDictionary(entry => entry.Template.TemplateId, entry => entry.Template);
    }

    public static TemplateVersionCatalogCreateResult Create(IEnumerable<TemplateCatalogEntry?>? entries)
    {
        if (entries is null)
        {
            return TemplateVersionCatalogCreateResult.Failure([
                new(TemplateCatalogValidationFailureReason.InvalidDependency,
                    "TEMPLATE_CATALOG_ENTRIES_MISSING", null, null, null)]);
        }

        var issues = ImmutableArray.CreateBuilder<TemplateCatalogValidationIssue>();
        var valid = new List<TemplateCatalogEntry>();
        var exact = new HashSet<(TemplateId, TemplateVersion)>();
        var current = new HashSet<TemplateId>();
        var ids = new HashSet<TemplateId>();
        var index = 0;
        foreach (var entry in entries)
        {
            if (entry?.Template.Identity is not { IsValid: true } identity)
            {
                issues.Add(new(TemplateCatalogValidationFailureReason.InvalidTemplate,
                    "TEMPLATE_CATALOG_TEMPLATE_INVALID", index, entry?.Template.TemplateId, entry?.Template.TemplateVersion));
            }
            else
            {
                ids.Add(identity.TemplateId);
                if (!exact.Add((identity.TemplateId, identity.Version)))
                {
                    issues.Add(new(TemplateCatalogValidationFailureReason.DuplicateVersion,
                        "TEMPLATE_CATALOG_VERSION_DUPLICATE", index, identity.TemplateId, identity.Version));
                }

                if (entry.IsCurrent && !current.Add(identity.TemplateId))
                {
                    issues.Add(new(TemplateCatalogValidationFailureReason.MultipleCurrentVersions,
                        "TEMPLATE_CATALOG_CURRENT_DUPLICATE", index, identity.TemplateId, identity.Version));
                }

                valid.Add(entry);
            }

            index++;
        }

        foreach (var id in ids.Where(id => !current.Contains(id)).OrderBy(id => id.Value, StringComparer.Ordinal))
        {
            issues.Add(new(TemplateCatalogValidationFailureReason.MissingCurrentVersion,
                "TEMPLATE_CATALOG_CURRENT_MISSING", null, id, null));
        }

        return issues.Count > 0
            ? TemplateVersionCatalogCreateResult.Failure(issues.ToImmutable())
            : TemplateVersionCatalogCreateResult.Success(new TemplateVersionCatalog(valid));
    }

    public bool TryGetExact(
        TemplateId templateId,
        TemplateVersion version,
        [NotNullWhen(true)] out AuditionArchiveTemplate? template)
    {
        if (!templateId.IsValid || !version.IsValid)
        {
            template = null;
            return false;
        }

        return _exact.TryGetValue((templateId, version), out template);
    }

    public bool TryGetCurrent(
        TemplateId templateId,
        [NotNullWhen(true)] out AuditionArchiveTemplate? template)
    {
        if (!templateId.IsValid)
        {
            template = null;
            return false;
        }

        return _current.TryGetValue(templateId, out template);
    }

    public TemplateResolutionResult Resolve(ArchiveTemplateReference snapshot)
    {
        if (snapshot?.Identity is not { IsValid: true } identity)
        {
            return new(TemplateResolutionStatus.InvalidSnapshot, null, null);
        }

        if (!TryGetCurrent(identity.TemplateId, out var current))
        {
            return new(TemplateResolutionStatus.TemplateMissing, null, null);
        }

        if (!TryGetExact(identity.TemplateId, identity.Version, out var exact))
        {
            return new(TemplateResolutionStatus.VersionMissing, null, current);
        }

        if (exact.ExpectedSha256 != identity.Sha256)
        {
            return new(TemplateResolutionStatus.HashMismatch, exact, current);
        }

        if (exact.CompatibleGameBuild != identity.CompatibleGameBuild)
        {
            return new(TemplateResolutionStatus.IncompatibleGameBuild, exact, current);
        }

        return new(
            exact.TemplateVersion == current.TemplateVersion
                ? TemplateResolutionStatus.ExactMatch
                : TemplateResolutionStatus.CurrentVersionDiffers,
            exact,
            current);
    }
}
