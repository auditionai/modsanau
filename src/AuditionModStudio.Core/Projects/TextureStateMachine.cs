using AuditionModStudio.Core.Mods;

namespace AuditionModStudio.Core.Projects;

public enum TextureState
{
    Original,
    Modified,
    AiGenerated,
    Pending,
    Invalid,
    Missing
}

public sealed record TextureRuntimeObservation(
    bool AssetExists,
    bool MetadataIsValid,
    bool OperationIsPending);

public enum TextureStateFailureReason
{
    None,
    InvalidProject,
    InvalidTexturePath,
    InvalidPreviousState
}

public sealed record TextureStateResult(
    bool Succeeded,
    TextureStateFailureReason FailureReason,
    string? DiagnosticCode,
    TextureState State,
    TextureState? PreviousState)
{
    public bool Changed => Succeeded && PreviousState is not null && PreviousState != State;

    public static TextureStateResult Success(TextureState state, TextureState? previousState) =>
        new(true, TextureStateFailureReason.None, null, state, previousState);

    public static TextureStateResult Failure(TextureStateFailureReason reason, string code) =>
        new(false, reason, code, default, null);
}

public interface ITextureStateMachine
{
    TextureStateResult Evaluate(
        AuditionProject project,
        ModRelativePath texturePath,
        TextureRuntimeObservation observation,
        TextureState? previousState = null);
}

public sealed class TextureStateMachine : ITextureStateMachine
{
    public TextureStateResult Evaluate(
        AuditionProject project,
        ModRelativePath texturePath,
        TextureRuntimeObservation observation,
        TextureState? previousState = null)
    {
        if (project is null || observation is null)
        {
            return TextureStateResult.Failure(
                TextureStateFailureReason.InvalidProject,
                "TEXTURE_STATE_INPUT_INVALID");
        }

        if (!texturePath.IsValid)
        {
            return TextureStateResult.Failure(
                TextureStateFailureReason.InvalidTexturePath,
                "TEXTURE_STATE_PATH_INVALID");
        }

        if (previousState is not null && !Enum.IsDefined(previousState.Value))
        {
            return TextureStateResult.Failure(
                TextureStateFailureReason.InvalidPreviousState,
                "TEXTURE_STATE_PREVIOUS_INVALID");
        }

        var state = Resolve(project, texturePath, observation);
        return TextureStateResult.Success(state, previousState);
    }

    private static TextureState Resolve(
        AuditionProject project,
        ModRelativePath texturePath,
        TextureRuntimeObservation observation)
    {
        if (!observation.AssetExists)
        {
            return TextureState.Missing;
        }

        if (!observation.MetadataIsValid)
        {
            return TextureState.Invalid;
        }

        if (observation.OperationIsPending)
        {
            return TextureState.Pending;
        }

        var edited = project.EditedTextures.FirstOrDefault(item =>
            string.Equals(item.RelativePath.Value, texturePath.Value, StringComparison.OrdinalIgnoreCase));
        if (edited is null)
        {
            return TextureState.Original;
        }

        return project.AiAssets.Any(asset => asset.Id == edited.CurrentImageAssetId)
            ? TextureState.AiGenerated
            : TextureState.Modified;
    }
}
