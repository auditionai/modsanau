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

public sealed record AiGenerateRequest(AiPrompt Prompt, AiTargetSize TargetSize);
public sealed record AiEditRequest(InternalImage Image, AiPrompt Prompt);
public sealed record AiInpaintRequest(InternalImage Image, InternalImage Mask, AiPrompt Prompt);
public sealed record AiOutpaintRequest(InternalImage Image, AiPrompt Prompt, AiTargetSize TargetSize);
public sealed record AiRemoveObjectRequest(InternalImage Image, InternalImage Mask);
public sealed record AiReplaceObjectRequest(InternalImage Image, InternalImage Mask, AiPrompt Prompt);
public sealed record AiUpscaleRequest(InternalImage Image, AiTargetSize TargetSize);

public enum AiOperationPhase
{
    Validating,
    Submitting,
    Processing,
    Receiving,
    Completed,
    Failed,
    Cancelled
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
