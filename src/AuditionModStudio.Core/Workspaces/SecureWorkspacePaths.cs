namespace AuditionModStudio.Core.Workspaces;

/// <summary>
/// Separates mutable archive work from extracted data and promoted build output.
/// Pristine templates are deliberately not part of this writable workspace model.
/// </summary>
public sealed record SecureWorkspacePaths(
    string RootDirectory,
    string WorkingDirectory,
    string ExtractedDirectory,
    string BuildOutputDirectory);
