using AuditionModStudio.Core.AI;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;

namespace Core.Tests;

public sealed class PromptPresetTests
{
    [Fact]
    public void Preset_is_provider_neutral_and_checks_exact_scope()
    {
        var preset = Create("studio-neon", 1, PromptPresetOrigin.Local);

        Assert.True(preset.IsApplicable(AiStudioOperation.Generate, new("audition"), new("ui"), new("logo")));
        Assert.False(preset.IsApplicable(AiStudioOperation.Edit, new("audition"), new("ui"), new("logo")));
        Assert.DoesNotContain(preset.GetType().GetProperties(), property =>
            property.Name.Contains("Provider", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Cost", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Model", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Invalid_name_prompt_version_and_empty_operations_are_rejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => Create("preset", 0, PromptPresetOrigin.Local));
        Assert.ThrowsAny<ArgumentException>(() => new PromptPreset(new("preset"), 1, "", "", new("valid"), null,
            [AiStudioOperation.Generate], new("audition"), new("ui"), new("logo"), [], PromptPresetOrigin.Local));
        Assert.ThrowsAny<ArgumentException>(() => new PromptPreset(new("preset"), 1, "Name", "", new("valid"), null,
            [], new("audition"), new("ui"), new("logo"), [], PromptPresetOrigin.Local));
        Assert.Throws<ArgumentException>(() => new AiPrompt(new string('x', AiPrompt.MaximumLength + 1)));
    }

    [Fact]
    public void Merge_uses_highest_version_and_is_deterministic()
    {
        var oldLocal = Create("b", 1, PromptPresetOrigin.Local);
        var currentCloud = Create("b", 2, PromptPresetOrigin.Cloud);
        var another = Create("a", 1, PromptPresetOrigin.Local);

        var first = PromptPresetMerge.Merge([oldLocal, another], [currentCloud]);
        var second = PromptPresetMerge.Merge([another, oldLocal], [currentCloud]);

        Assert.Equal(["a", "b"], first.Presets.Select(item => item.Id.Value));
        Assert.Equal(first.Presets.Select(item => (item.Id, item.Version)), second.Presets.Select(item => (item.Id, item.Version)));
        Assert.Equal(2, first.Presets[1].Version);
    }

    [Fact]
    public void Same_identity_and_version_with_different_content_is_isolated()
    {
        var local = Create("conflict", 2, PromptPresetOrigin.Local);
        var cloud = new PromptPreset(new("conflict"), 2, "Different", "", new("other prompt"), null,
            [AiStudioOperation.Generate], new("audition"), new("ui"), new("logo"), [], PromptPresetOrigin.Cloud);

        var result = PromptPresetMerge.Merge([local], [cloud]);

        Assert.Empty(result.Presets);
        Assert.Contains(result.Issues, issue => issue.DiagnosticCode == "PROMPT_PRESET_VERSION_CONFLICT");
    }

    private static PromptPreset Create(string id, int version, PromptPresetOrigin origin) => new(new(id), version,
        "Neon", "UI logo", new("bright neon logo"), new AiPrompt("blur"), [AiStudioOperation.Generate],
        new("audition"), new("ui"), new("logo"), ["neon"], origin);
}
