using AuditionModStudio.Core.AI;

namespace AuditionModStudio.App.AiStudio;

public sealed record AiStudioOperationOption(
    AiStudioOperation Operation, string Label, string Description, bool RequiresReference);

public sealed record AiStudioOption(string Value, string Label);

public sealed record AiStudioAspectOption(string Value, string Label, int Width, int Height);

public static class AiStudioOptions
{
    public static IReadOnlyList<AiStudioOperationOption> Operations { get; } =
    [
        new(AiStudioOperation.Generate, "Generate", "Create a new image from a prompt.", false),
        new(AiStudioOperation.Edit, "Edit reference", "Transform the selected project texture.", true),
        new(AiStudioOperation.Outpaint, "Outpaint", "Extend the selected project texture.", true),
        new(AiStudioOperation.Upscale, "Upscale", "Increase detail while preserving the selected texture.", true),
    ];

    public static IReadOnlyList<AiStudioOption> Models { get; } =
    [
        new("server-default", "Server default"),
        new("balanced", "Balanced"),
        new("detail", "Detail"),
    ];

    public static IReadOnlyList<AiStudioOption> Qualities { get; } =
    [
        new("standard", "Standard"),
        new("high", "High"),
    ];

    public static IReadOnlyList<AiStudioAspectOption> Aspects { get; } =
    [
        new("square", "Square · 1:1", 512, 512),
        new("landscape", "Landscape · 3:2", 768, 512),
        new("portrait", "Portrait · 2:3", 512, 768),
    ];
}
