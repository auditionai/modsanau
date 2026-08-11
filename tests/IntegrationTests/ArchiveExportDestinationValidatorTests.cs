using AuditionModStudio.Core.Exports;
using AuditionModStudio.Core.Paths;
using AuditionModStudio.Infrastructure.Exports;
using AuditionModStudio.Infrastructure.Paths;

namespace IntegrationTests;

public sealed class ArchiveExportDestinationValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "PLAN 51 Export Đích", Guid.NewGuid().ToString("N"));

    public ArchiveExportDestinationValidatorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Canonical_unicode_destination_with_spaces_is_valid()
    {
        var nested = Path.Combine(_root, "Thư mục xuất file");
        Directory.CreateDirectory(nested);

        var result = Validator().Validate(Request(nested + Path.DirectorySeparatorChar, "Bản dựng 015.ab"));

        Assert.True(result.Succeeded);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(nested)),
            result.Destination!.CanonicalDirectory);
        Assert.Equal(Path.Combine(nested, "Bản dựng 015.ab"), result.Destination.FullPath);
        Assert.False(result.Destination.ExistingFile);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("   ")]
    public void Invalid_or_relative_directory_is_rejected(string path)
    {
        var result = Validator().Validate(Request(path, "015.ab"));
        Assert.Equal(ArchiveExportDestinationFailureReason.InvalidDirectoryPath, result.FailureReason);
    }

    [Fact]
    public void Missing_directory_is_distinct()
    {
        var result = Validator().Validate(Request(Path.Combine(_root, "missing"), "015.ab"));
        Assert.Equal(ArchiveExportDestinationFailureReason.DirectoryNotFound, result.FailureReason);
    }

    [Fact]
    public void File_instead_of_directory_is_rejected()
    {
        var file = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(file, "x");
        var result = Validator().Validate(Request(file, "015.ab"));
        Assert.Equal(ArchiveExportDestinationFailureReason.InvalidDirectoryPath, result.FailureReason);
    }

    [Theory]
    [InlineData("../015.ab")]
    [InlineData("folder/015.ab")]
    [InlineData("CON.ab")]
    [InlineData("015.ab.")]
    [InlineData("015.ab ")]
    public void Unsafe_filename_is_rejected(string fileName)
    {
        var result = Validator().Validate(Request(_root, fileName));
        Assert.Equal(ArchiveExportDestinationFailureReason.InvalidFileName, result.FailureReason);
    }

    [Fact]
    public void Extension_must_match_trusted_archive_contract()
    {
        var result = Validator().Validate(Request(_root, "015.acv"));
        Assert.Equal(ArchiveExportDestinationFailureReason.InvalidArchiveExtension, result.FailureReason);
    }

    [Fact]
    public void Future_extension_comes_from_contract_not_hard_coded_branch()
    {
        Assert.True(ArchiveExportFileContract.TryCreate("texture.future", out var contract));
        var result = Validator().Validate(new(
            _root, "export.future", contract, ArchiveExportOverwritePolicy.RejectExisting));
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Existing_file_requires_explicit_overwrite_policy()
    {
        File.WriteAllText(Path.Combine(_root, "015.ab"), "old");

        var rejected = Validator().Validate(Request(_root, "015.ab"));
        var accepted = Validator().Validate(Request(
            _root, "015.ab", ArchiveExportOverwritePolicy.ReplaceExisting));

        Assert.Equal(ArchiveExportDestinationFailureReason.DestinationCollision, rejected.FailureReason);
        Assert.True(accepted.Succeeded);
        Assert.True(accepted.Destination!.ExistingFile);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_root, "015.ab")));
    }

    [Fact]
    public void Access_denied_is_not_reported_as_missing()
    {
        var fileSystem = new StubFileSystem { AttributesException = new UnauthorizedAccessException() };
        var result = Validator(fileSystem).Validate(Request(_root, "015.ab"));
        Assert.Equal(ArchiveExportDestinationFailureReason.DirectoryAccessDenied, result.FailureReason);
    }

    [Fact]
    public void Unavailable_directory_is_structured()
    {
        var fileSystem = new StubFileSystem { AttributesException = new IOException() };
        var result = Validator(fileSystem).Validate(Request(_root, "015.ab"));
        Assert.Equal(ArchiveExportDestinationFailureReason.DirectoryUnavailable, result.FailureReason);
    }

    [Fact]
    public void Reparse_rejection_is_structured()
    {
        var result = Validator(pathSecurity: new RejectingPathSecurity())
            .Validate(Request(_root, "015.ab"));
        Assert.Equal(ArchiveExportDestinationFailureReason.ReparsePointNotAllowed, result.FailureReason);
    }

    [Theory]
    [InlineData("CON.ab")]
    [InlineData("archive")]
    [InlineData("archive.ab.")]
    public void Invalid_trusted_archive_contract_is_rejected(string fileName)
    {
        Assert.False(ArchiveExportFileContract.TryCreate(fileName, out _));
    }

    [Fact]
    public void Validation_does_not_mutate_destination()
    {
        var before = Directory.EnumerateFileSystemEntries(_root).ToArray();
        var result = Validator().Validate(Request(_root, "015.ab"));
        var after = Directory.EnumerateFileSystemEntries(_root).ToArray();
        Assert.True(result.Succeeded);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Concurrent_validation_is_deterministic()
    {
        var validator = Validator();
        var results = Enumerable.Range(0, 64)
            .AsParallel()
            .Select(_ => validator.Validate(Request(_root, "015.ab")))
            .ToArray();
        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Single(results.Select(result => result.Destination!.FullPath).Distinct());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ArchiveExportDestinationRequest Request(
        string directory,
        string fileName,
        ArchiveExportOverwritePolicy policy = ArchiveExportOverwritePolicy.RejectExisting)
    {
        Assert.True(ArchiveExportFileContract.TryCreate("015.ab", out var contract));
        return new(directory, fileName, contract, policy);
    }

    private static ArchiveExportDestinationValidator Validator(
        IExportDestinationFileSystem? fileSystem = null,
        IPathSecurity? pathSecurity = null) =>
        new(pathSecurity ?? new PathSecurity(), fileSystem ?? new SystemExportDestinationFileSystem());

    private sealed class StubFileSystem : IExportDestinationFileSystem
    {
        public Exception? AttributesException { get; init; }

        public FileAttributes GetAttributes(string path) =>
            AttributesException is null ? FileAttributes.Directory : throw AttributesException;

        public bool FileExists(string path) => false;

        public bool DirectoryExists(string path) => false;
    }

    private sealed class RejectingPathSecurity : IPathSecurity
    {
        public string ResolvePathWithinRoot(string rootDirectory, string relativePath) =>
            Path.Combine(rootDirectory, relativePath);

        public void EnsureNoReparsePoints(string rootDirectory, string candidatePath) =>
            throw new InvalidOperationException();
    }
}
