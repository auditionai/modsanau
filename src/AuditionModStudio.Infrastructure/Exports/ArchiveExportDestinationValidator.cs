using System.Text;
using AuditionModStudio.Core.Exports;
using AuditionModStudio.Core.Paths;

namespace AuditionModStudio.Infrastructure.Exports;

public interface IExportDestinationFileSystem
{
    FileAttributes GetAttributes(string path);

    bool FileExists(string path);

    bool DirectoryExists(string path);
}

public sealed class SystemExportDestinationFileSystem : IExportDestinationFileSystem
{
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);
}

public sealed class ArchiveExportDestinationValidator(
    IPathSecurity pathSecurity,
    IExportDestinationFileSystem fileSystem) : IArchiveExportDestinationValidator
{
    public ArchiveExportDestinationResult Validate(ArchiveExportDestinationRequest request)
    {
        if (request is null || !request.FileContract.IsValid || !Enum.IsDefined(request.OverwritePolicy))
        {
            return Fail(ArchiveExportDestinationFailureReason.InvalidRequest,
                "ARCHIVE_EXPORT_DESTINATION_REQUEST_INVALID");
        }

        if (!TryCanonicalizeDirectory(request.OutputDirectory, out var directory, out var pathFailure))
        {
            return pathFailure!;
        }

        var directoryAccess = ValidateDirectory(directory!);
        if (directoryAccess is not null)
        {
            return directoryAccess;
        }

        try
        {
            var root = Path.GetPathRoot(directory!)!;
            pathSecurity.EnsureNoReparsePoints(root, directory!);
        }
        catch (InvalidOperationException)
        {
            return Fail(ArchiveExportDestinationFailureReason.ReparsePointNotAllowed,
                "ARCHIVE_EXPORT_DESTINATION_REPARSE_POINT");
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(ArchiveExportDestinationFailureReason.DirectoryAccessDenied,
                "ARCHIVE_EXPORT_DESTINATION_ACCESS_DENIED");
        }
        catch (IOException)
        {
            return Fail(ArchiveExportDestinationFailureReason.DirectoryUnavailable,
                "ARCHIVE_EXPORT_DESTINATION_UNAVAILABLE");
        }

        if (!TryResolveFile(directory!, request.OutputFileName, out var fileName, out var fullPath))
        {
            return Fail(ArchiveExportDestinationFailureReason.InvalidFileName,
                "ARCHIVE_EXPORT_DESTINATION_FILENAME_INVALID");
        }

        if (!Path.GetExtension(fileName!).Equals(
                request.FileContract.Extension, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(ArchiveExportDestinationFailureReason.InvalidArchiveExtension,
                "ARCHIVE_EXPORT_DESTINATION_EXTENSION_MISMATCH");
        }

        bool fileExists;
        try
        {
            if (fileSystem.DirectoryExists(fullPath!))
            {
                return Fail(ArchiveExportDestinationFailureReason.DestinationCollision,
                    "ARCHIVE_EXPORT_DESTINATION_DIRECTORY_COLLISION");
            }

            fileExists = fileSystem.FileExists(fullPath!);
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(ArchiveExportDestinationFailureReason.DirectoryAccessDenied,
                "ARCHIVE_EXPORT_DESTINATION_ACCESS_DENIED");
        }
        catch (IOException)
        {
            return Fail(ArchiveExportDestinationFailureReason.DirectoryUnavailable,
                "ARCHIVE_EXPORT_DESTINATION_UNAVAILABLE");
        }

        if (fileExists && request.OverwritePolicy == ArchiveExportOverwritePolicy.RejectExisting)
        {
            return Fail(ArchiveExportDestinationFailureReason.DestinationCollision,
                "ARCHIVE_EXPORT_DESTINATION_EXISTS");
        }

        return ArchiveExportDestinationResult.Success(new(
            directory!, fileName!, fullPath!, request.FileContract, request.OverwritePolicy, fileExists));
    }

    private static bool TryCanonicalizeDirectory(
        string? value,
        out string? canonical,
        out ArchiveExportDestinationResult? failure)
    {
        canonical = null;
        failure = null;
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            failure = Fail(ArchiveExportDestinationFailureReason.InvalidDirectoryPath,
                "ARCHIVE_EXPORT_DESTINATION_DIRECTORY_INVALID");
            return false;
        }

        try
        {
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                          or PathTooLongException)
        {
            failure = Fail(ArchiveExportDestinationFailureReason.InvalidDirectoryPath,
                "ARCHIVE_EXPORT_DESTINATION_DIRECTORY_INVALID");
            return false;
        }
    }

    private ArchiveExportDestinationResult? ValidateDirectory(string directory)
    {
        try
        {
            var attributes = fileSystem.GetAttributes(directory);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                return Fail(ArchiveExportDestinationFailureReason.InvalidDirectoryPath,
                    "ARCHIVE_EXPORT_DESTINATION_NOT_DIRECTORY");
            }
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(ArchiveExportDestinationFailureReason.DirectoryAccessDenied,
                "ARCHIVE_EXPORT_DESTINATION_ACCESS_DENIED");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Fail(ArchiveExportDestinationFailureReason.DirectoryNotFound,
                "ARCHIVE_EXPORT_DESTINATION_DIRECTORY_MISSING");
        }
        catch (IOException)
        {
            return Fail(ArchiveExportDestinationFailureReason.DirectoryUnavailable,
                "ARCHIVE_EXPORT_DESTINATION_UNAVAILABLE");
        }

        return null;
    }

    private bool TryResolveFile(
        string directory,
        string? requestedFileName,
        out string? normalizedFileName,
        out string? fullPath)
    {
        normalizedFileName = null;
        fullPath = null;
        if (string.IsNullOrWhiteSpace(requestedFileName)
            || !string.Equals(Path.GetFileName(requestedFileName), requestedFileName,
                StringComparison.Ordinal)
            || requestedFileName.EndsWith(' ')
            || requestedFileName.EndsWith('.'))
        {
            return false;
        }

        try
        {
            normalizedFileName = requestedFileName.Normalize(NormalizationForm.FormC);
            fullPath = pathSecurity.ResolvePathWithinRoot(directory, normalizedFileName);
            return string.Equals(Path.GetDirectoryName(fullPath), directory,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static ArchiveExportDestinationResult Fail(
        ArchiveExportDestinationFailureReason reason,
        string code) => ArchiveExportDestinationResult.Failure(reason, code);
}
