using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace AuditionModStudio.Core.Archives;

public readonly record struct TemplateId
{
    public const int MaximumLength = 128;

    public TemplateId(string value)
    {
        if (!IsValidValue(value))
        {
            throw new ArgumentException("Template IDs must be bounded lowercase ASCII stable identifiers.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }
    public bool IsValid => IsValidValue(Value);
    public override string ToString() => Value ?? string.Empty;

    private static bool IsValidValue(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaximumLength
        && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
}

public readonly record struct TemplateVersion
{
    public const int MaximumLength = 64;

    public TemplateVersion(string value)
    {
        if (!IsValidValue(value))
        {
            throw new ArgumentException("Template versions must be bounded culture-independent opaque identifiers.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }
    public bool IsValid => IsValidValue(Value);
    public override string ToString() => Value ?? string.Empty;

    private static bool IsValidValue(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaximumLength
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}

public readonly record struct Sha256Digest
{
    public Sha256Digest(string value)
    {
        if (value is null || value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException("SHA-256 digests must contain exactly 64 hexadecimal characters.", nameof(value));
        }

        Value = value.ToUpperInvariant();
    }

    public string Value { get; }
    public bool IsValid => Value is not null && Value.Length == 64 && Value.All(char.IsAsciiHexDigit);
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct TemplateSha256
{
    public TemplateSha256(string value) => Digest = new(value);

    public Sha256Digest Digest { get; }
    public string Value => Digest.Value;
    public bool IsValid => Digest.IsValid;
    public override string ToString() => Digest.ToString();
}

public readonly record struct CompatibleGameBuild
{
    public const int MaximumLength = 128;

    public CompatibleGameBuild(string value)
    {
        if (!IsValidValue(value))
        {
            throw new ArgumentException("Compatible game builds must be bounded culture-independent identifiers.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }
    public bool IsValid => IsValidValue(Value);
    public override string ToString() => Value ?? string.Empty;

    private static bool IsValidValue(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaximumLength
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}

public sealed record TemplateIdentity(
    TemplateId TemplateId,
    TemplateVersion Version,
    TemplateSha256 Sha256,
    CompatibleGameBuild CompatibleGameBuild)
{
    public bool IsValid => TemplateId.IsValid && Version.IsValid && Sha256.IsValid && CompatibleGameBuild.IsValid;
}

public sealed record TemplateCatalogEntry(AuditionArchiveTemplate Template, bool IsCurrent);

public interface ITemplateVersionCatalog
{
    bool TryGetExact(
        TemplateId templateId,
        TemplateVersion version,
        [NotNullWhen(true)] out AuditionArchiveTemplate? template);

    bool TryGetCurrent(
        TemplateId templateId,
        [NotNullWhen(true)] out AuditionArchiveTemplate? template);

    TemplateResolutionResult Resolve(Projects.ArchiveTemplateReference snapshot);
}

public enum TemplateResolutionStatus
{
    ExactMatch,
    CurrentVersionDiffers,
    InvalidSnapshot,
    TemplateMissing,
    VersionMissing,
    HashMismatch,
    IncompatibleGameBuild
}

public sealed record TemplateResolutionResult(
    TemplateResolutionStatus Status,
    AuditionArchiveTemplate? ExactTemplate,
    AuditionArchiveTemplate? CurrentTemplate);

public enum TemplateCatalogValidationFailureReason
{
    InvalidDependency,
    InvalidTemplate,
    DuplicateVersion,
    MissingCurrentVersion,
    MultipleCurrentVersions
}

public sealed record TemplateCatalogValidationIssue(
    TemplateCatalogValidationFailureReason Reason,
    string DiagnosticCode,
    int? EntryIndex,
    TemplateId? TemplateId,
    TemplateVersion? Version);

public sealed record TemplateVersionCatalogCreateResult(
    bool Succeeded,
    ImmutableArray<TemplateCatalogValidationIssue> Issues,
    ITemplateVersionCatalog? Catalog)
{
    public static TemplateVersionCatalogCreateResult Success(ITemplateVersionCatalog catalog) => new(true, [], catalog);
    public static TemplateVersionCatalogCreateResult Failure(ImmutableArray<TemplateCatalogValidationIssue> issues) =>
        new(false, issues, null);
}
