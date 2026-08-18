using AuditionModStudio.Core.Images;

namespace AuditionModStudio.Core.AI;

public readonly record struct AiPrompt
{
    public const int MaximumLength = 4_000;

    public AiPrompt(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumLength
            || value.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
        {
            throw new ArgumentException("AI prompts must be non-empty bounded text.", nameof(value));
        }

        Value = value.Trim();
    }

    public string Value { get; }
    public bool IsValid => !string.IsNullOrWhiteSpace(Value)
                           && Value.Length <= MaximumLength
                           && !Value.Any(character => char.IsControl(character)
                               && character is not '\r' and not '\n' and not '\t');
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct AiTargetSize
{
    public const int MaximumDimension = 16_384;

    public AiTargetSize(int width, int height)
    {
        if (width is <= 0 or > MaximumDimension || height is <= 0 or > MaximumDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "AI target dimensions are outside the supported bounds.");
        }

        Width = width;
        Height = height;
    }

    public int Width { get; }
    public int Height { get; }
    public bool IsValid => Width is > 0 and <= MaximumDimension && Height is > 0 and <= MaximumDimension;
}

public sealed record AiRequestPreferences(AiPrompt? NegativePrompt, string Model, string Quality)
{
    public bool IsValid => IsSafeOption(Model) && IsSafeOption(Quality);

    private static bool IsSafeOption(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 64
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}

public sealed record AiGenerateRequest(
    AiPrompt Prompt, AiTargetSize TargetSize, AiRequestPreferences? Preferences = null);
public sealed record AiEditRequest(
    InternalImage Image, AiPrompt Prompt, AiRequestPreferences? Preferences = null);
public sealed record AiInpaintRequest(
    InternalImage Image, InternalImage Mask, AiPrompt Prompt, AiRequestPreferences? Preferences = null);
public sealed record AiOutpaintRequest(
    InternalImage Image, AiPrompt Prompt, AiTargetSize TargetSize, AiRequestPreferences? Preferences = null);
public sealed record AiRemoveObjectRequest(InternalImage Image, InternalImage Mask);
public sealed record AiReplaceObjectRequest(
    InternalImage Image, InternalImage Mask, AiPrompt Prompt, AiRequestPreferences? Preferences = null);
public sealed record AiUpscaleRequest(
    InternalImage Image, AiTargetSize TargetSize, AiRequestPreferences? Preferences = null);

public enum AiOperationPhase
{
    Validating,
    Submitting,
    Processing,
    Receiving,
    Completed,
    Failed,
    Cancelled,
    ReconciliationRequired
}

public sealed record AiOperationProgress(
    AiOperationPhase Phase,
    int Percentage,
    string DiagnosticCode);

public enum AiServiceFailureReason
{
    None,
    InvalidRequest,
    Unavailable,
    Rejected,
    Failed,
    Cancelled
}

public sealed record AiImageResult(
    bool Succeeded,
    bool Cancelled,
    AiServiceFailureReason FailureReason,
    string DiagnosticCode,
    InternalImage? Image)
{
    public static AiImageResult Success(InternalImage image) =>
        new(true, false, AiServiceFailureReason.None, "AI_OPERATION_COMPLETED", image);

    public static AiImageResult Failure(AiServiceFailureReason reason, string code) =>
        new(false, false, reason, code, null);

    public static AiImageResult CancelledResult() =>
        new(false, true, AiServiceFailureReason.Cancelled, "AI_OPERATION_CANCELLED", null);
}

public interface IAiService
{
    Task<AiImageResult> GenerateAsync(AiGenerateRequest request, IProgress<AiOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<AiImageResult> EditAsync(AiEditRequest request, IProgress<AiOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<AiImageResult> InpaintAsync(AiInpaintRequest request, IProgress<AiOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<AiImageResult> OutpaintAsync(AiOutpaintRequest request, IProgress<AiOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<AiImageResult> RemoveObjectAsync(AiRemoveObjectRequest request, IProgress<AiOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<AiImageResult> ReplaceObjectAsync(AiReplaceObjectRequest request, IProgress<AiOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<AiImageResult> UpscaleAsync(AiUpscaleRequest request, IProgress<AiOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public enum AiStudioOperation
{
    Generate,
    Edit,
    Inpaint,
    Outpaint,
    RemoveObject,
    ReplaceObject,
    Upscale
}

public sealed record AiStudioQuote(long CreditCost, string PricingVersion);
public sealed record AiStudioQuoteResult(
    bool Succeeded, string DiagnosticCode, AiStudioQuote? Quote);

public enum AiStudioJobState
{
    Pending,
    Queued,
    Processing,
    Completed,
    Failed,
    Cancelled
}

public sealed record AiStudioJobSummary(
    Guid JobId,
    AiStudioOperation Operation,
    AiStudioJobState State,
    long ReservedCredits,
    long? FinalCredits,
    DateTimeOffset CreatedAt,
    bool CanCancel);

public sealed record AiStudioHistoryResult(
    bool Succeeded, string DiagnosticCode, IReadOnlyList<AiStudioJobSummary> Jobs);

public sealed record AiStudioModelOption(
    string Id,
    string Name,
    IReadOnlyList<string> Qualities,
    IReadOnlyList<string> AspectRatios,
    IReadOnlyList<string> Resolutions,
    IReadOnlyList<long> CreditCosts)
{
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Settings { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
}

public sealed record AiStudioModelsResult(
    bool Succeeded, string DiagnosticCode, IReadOnlyList<AiStudioModelOption> Models);

public sealed record AiStudioExecutionRequest(
    AiStudioOperation Operation,
    InternalImage Source,
    AiMask? Mask,
    AiPrompt? Prompt,
    AiTargetSize? TargetSize,
    string PublicOptionId,
    string IdempotencyKey,
    IReadOnlyList<InternalImage>? AdditionalReferences = null,
    IReadOnlyDictionary<string, string>? ModelSettings = null);

public sealed record AiStudioExecutionResult(
    bool Succeeded, bool Cancelled, string DiagnosticCode, InternalImage? Preview);

public interface IAiTransportImageEncoder
{
    Task<byte[]?> EncodePngAsync(InternalImage image, CancellationToken cancellationToken = default);
    Task<byte[]?> EncodeMaskPngAsync(AiMask mask, CancellationToken cancellationToken = default);
}

public interface IAiStudioService
{
    bool SupportsGenerationWithReferences => false;

    Task<AiStudioModelsResult> GetModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AiStudioModelsResult(false, "AI_MODELS_UNAVAILABLE", []));

    Task<AiStudioQuoteResult> GetQuoteAsync(
        AiStudioOperation operation,
        CancellationToken cancellationToken = default);

    Task<AiStudioHistoryResult> GetHistoryAsync(CancellationToken cancellationToken = default);

    Task<AiStudioHistoryResult> CancelJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);

    Task<AiStudioExecutionResult> ExecuteAsync(
        AiStudioExecutionRequest request,
        IProgress<AiOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) => Task.FromResult(new AiStudioExecutionResult(
            false, cancellationToken.IsCancellationRequested,
            cancellationToken.IsCancellationRequested ? "AI_STUDIO_CANCELLED" : "AI_STUDIO_UNAVAILABLE", null));
}
