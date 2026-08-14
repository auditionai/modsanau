using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuditionModStudio.Core.AI;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Paths;

namespace AuditionModStudio.Projects;

public sealed class LocalPromptPresetStore(IAppPaths appPaths, IPathSecurity pathSecurity) : ILocalPromptPresetStore
{
    private const string DirectoryName = "PromptPresets";
    private const string FileName = "presets.v1.json";
    private const long MaximumBytes = 2 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) },
    };

    public async Task<PromptPresetCollectionResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCoreAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Failure("PROMPT_PRESET_LOAD_CANCELLED"); }
        catch (Exception exception) when (IsStorageFailure(exception)) { return Failure("PROMPT_PRESET_STORAGE_CORRUPT"); }
        finally { _gate.Release(); }
    }

    public async Task<PromptPresetMutationResult> SaveAsync(PromptPreset preset, CancellationToken cancellationToken = default)
    {
        if (preset is null || preset.Origin != PromptPresetOrigin.Local) return new(false, "PROMPT_PRESET_LOCAL_REQUIRED");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!loaded.Succeeded) return new(false, loaded.DiagnosticCode);
            var existing = loaded.Presets.FirstOrDefault(item => item.Id == preset.Id);
            if (existing is not null && preset.Version <= existing.Version) return new(false, "PROMPT_PRESET_VERSION_NOT_NEWER");
            var values = loaded.Presets.Where(item => item.Id != preset.Id).Append(preset);
            await WriteAsync(values, cancellationToken).ConfigureAwait(false);
            return new(true, "PROMPT_PRESET_SAVED");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new(false, "PROMPT_PRESET_SAVE_CANCELLED"); }
        catch (Exception exception) when (IsStorageFailure(exception)) { return new(false, "PROMPT_PRESET_SAVE_FAILED"); }
        finally { _gate.Release(); }
    }

    public async Task<PromptPresetMutationResult> DeleteAsync(PromptPresetId id, CancellationToken cancellationToken = default)
    {
        if (!id.IsValid) return new(false, "PROMPT_PRESET_ID_INVALID");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!loaded.Succeeded) return new(false, loaded.DiagnosticCode);
            if (!loaded.Presets.Any(item => item.Id == id)) return new(false, "PROMPT_PRESET_NOT_FOUND");
            await WriteAsync(loaded.Presets.Where(item => item.Id != id), cancellationToken).ConfigureAwait(false);
            return new(true, "PROMPT_PRESET_DELETED");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new(false, "PROMPT_PRESET_DELETE_CANCELLED"); }
        catch (Exception exception) when (IsStorageFailure(exception)) { return new(false, "PROMPT_PRESET_DELETE_FAILED"); }
        finally { _gate.Release(); }
    }

    public async Task<PromptPresetCollectionResult> ImportAsync(Stream input, CancellationToken cancellationToken = default)
    {
        if (input is null || !input.CanRead) return Failure("PROMPT_PRESET_IMPORT_INVALID");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadDocumentAsync(input, cancellationToken).ConfigureAwait(false);
            var presets = ValidateDocument(document, requireLocal: true);
            await WriteAsync(presets, cancellationToken).ConfigureAwait(false);
            return new(true, "PROMPT_PRESETS_IMPORTED", presets, []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Failure("PROMPT_PRESET_IMPORT_CANCELLED"); }
        catch (Exception exception) when (IsStorageFailure(exception)) { return Failure("PROMPT_PRESET_IMPORT_REJECTED"); }
        finally { _gate.Release(); }
    }

    public async Task<PromptPresetMutationResult> ExportAsync(Stream output, CancellationToken cancellationToken = default)
    {
        if (output is null || !output.CanWrite) return new(false, "PROMPT_PRESET_EXPORT_INVALID");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!loaded.Succeeded) return new(false, loaded.DiagnosticCode);
            await JsonSerializer.SerializeAsync(output, ToDocument(loaded.Presets), Options, cancellationToken).ConfigureAwait(false);
            return new(true, "PROMPT_PRESETS_EXPORTED");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new(false, "PROMPT_PRESET_EXPORT_CANCELLED"); }
        catch (Exception exception) when (IsStorageFailure(exception)) { return new(false, "PROMPT_PRESET_EXPORT_FAILED"); }
        finally { _gate.Release(); }
    }

    private async Task<PromptPresetCollectionResult> LoadCoreAsync(CancellationToken cancellationToken)
    {
        var directory = GetDirectory(); var path = pathSecurity.ResolvePathWithinRoot(directory, FileName);
        if (!File.Exists(path)) return new(true, "PROMPT_PRESETS_EMPTY", [], []);
        pathSecurity.EnsureNoReparsePoints(appPaths.SettingsDirectory, path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var presets = ValidateDocument(await ReadDocumentAsync(stream, cancellationToken).ConfigureAwait(false), true);
        return new(true, "PROMPT_PRESETS_LOADED", presets, []);
    }

    private async Task WriteAsync(IEnumerable<PromptPreset> presets, CancellationToken cancellationToken)
    {
        var directory = GetDirectory(); Directory.CreateDirectory(directory);
        pathSecurity.EnsureNoReparsePoints(appPaths.SettingsDirectory, directory);
        var destination = pathSecurity.ResolvePathWithinRoot(directory, FileName);
        var temporary = pathSecurity.ResolvePathWithinRoot(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, ToDocument(presets), Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false); stream.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string GetDirectory() => pathSecurity.ResolvePathWithinRoot(appPaths.SettingsDirectory, DirectoryName);
    private static PresetDocument ToDocument(IEnumerable<PromptPreset> presets) => new(PromptPreset.CurrentSchemaVersion,
        presets.OrderBy(item => item.Id.Value, StringComparer.Ordinal).Select(PresetItem.FromModel).ToImmutableArray());

    private static async Task<PresetDocument> ReadDocumentAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream.CanSeek && stream.Length > MaximumBytes) throw new InvalidDataException();
        await using var bounded = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > MaximumBytes) throw new InvalidDataException("Preset document exceeds the import limit.");
            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        bounded.Position = 0;
        var document = await JsonSerializer.DeserializeAsync<PresetDocument>(bounded, Options, cancellationToken).ConfigureAwait(false);
        return document ?? throw new InvalidDataException();
    }

    private static ImmutableArray<PromptPreset> ValidateDocument(PresetDocument document, bool requireLocal)
    {
        if (document.SchemaVersion != PromptPreset.CurrentSchemaVersion || document.Presets.IsDefault
            || document.Presets.Length > 1_000) throw new InvalidDataException("Unsupported or invalid preset schema.");
        var presets = document.Presets.Select(item => item.ToModel()).ToImmutableArray();
        if (requireLocal && presets.Any(item => item.Origin != PromptPresetOrigin.Local)
            || presets.Select(item => item.Id).Distinct().Count() != presets.Length) throw new InvalidDataException();
        return presets.OrderBy(item => item.Id.Value, StringComparer.Ordinal).ToImmutableArray();
    }

    private static bool IsStorageFailure(Exception exception) => exception is IOException or UnauthorizedAccessException
        or InvalidDataException or InvalidOperationException or ArgumentException or JsonException or NotSupportedException;
    private static PromptPresetCollectionResult Failure(string code) => new(false, code, [], []);

    private sealed record PresetDocument(int SchemaVersion, ImmutableArray<PresetItem> Presets);
    private sealed record PresetItem(string Id, int Version, string Name, string Description, string Prompt,
        string? NegativePrompt, ImmutableArray<AiStudioOperation> ApplicableOperations, string GameId, string ModId,
        string TextureSemanticType, ImmutableArray<string> Tags, PromptPresetOrigin Origin)
    {
        public static PresetItem FromModel(PromptPreset value) => new(value.Id.Value, value.Version, value.Name,
            value.Description, value.Prompt.Value, value.NegativePrompt?.Value, value.ApplicableOperations,
            value.GameId.Value, value.ModId.Value, value.TextureSemanticType.Value, value.Tags, value.Origin);
        public PromptPreset ToModel() => new(new(Id), Version, Name, Description, new(Prompt),
            NegativePrompt is null ? null : new AiPrompt(NegativePrompt), ApplicableOperations,
            new GameId(GameId), new ModId(ModId), new TextureSlotId(TextureSemanticType), Tags, Origin);
    }
}
