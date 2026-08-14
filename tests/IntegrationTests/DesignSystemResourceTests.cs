using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace IntegrationTests;

public sealed partial class DesignSystemResourceTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void App_merges_three_token_layers_before_component_styles()
    {
        var document = Load("src", "AuditionModStudio.App", "App.xaml");
        var sources = document.Descendants(Presentation + "ResourceDictionary")
            .Select(element => (string?)element.Attribute("Source"))
            .Where(source => source is not null)
            .Select(source => source!)
            .ToArray();

        Assert.Equal([
            "DesignSystem/PrimitiveTokens.xaml",
            "DesignSystem/SemanticTokens.xaml",
            "DesignSystem/ComponentTokens.xaml",
            "DesignSystem/Components.xaml",
        ], sources);
    }

    [Fact]
    public void Primitive_tokens_are_unique_raw_values_without_semantic_references()
    {
        var document = LoadDesignSystem("PrimitiveTokens.xaml");
        var keys = ResourceKeys(document);

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("AmsSpace4", keys);
        Assert.Contains("AmsRadiusLarge", keys);
        Assert.Contains("AmsTypeDisplay", keys);
        Assert.DoesNotContain("{ThemeResource", document.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("{StaticResource", document.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Dark_light_and_high_contrast_themes_publish_the_same_semantic_contract()
    {
        var document = LoadDesignSystem("SemanticTokens.xaml");
        var dictionaries = document.Descendants(Presentation + "ResourceDictionary")
            .Where(element => element.Attribute(Xaml + "Key") is not null)
            .ToDictionary(
                element => (string)element.Attribute(Xaml + "Key")!,
                element => ResourceKeys(element));

        Assert.Equal(["Dark", "Default", "HighContrast", "Light"],
            dictionaries.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal(dictionaries["Default"].Order(), dictionaries["Light"].Order());
        Assert.Equal(dictionaries["Default"].Order(), dictionaries["Dark"].Order());
        Assert.Equal(dictionaries["Default"].Order(), dictionaries["HighContrast"].Order());
        Assert.Contains("AmsFocusColor", dictionaries["Default"]);
        Assert.Contains("AmsTextPrimaryColor", dictionaries["Default"]);
        Assert.DoesNotContain(document.Descendants(Presentation + "Color"), element =>
            element.Value.Contains("{StaticResource", StringComparison.Ordinal));
    }

    [Fact]
    public void Component_tokens_reference_semantics_and_styles_do_not_hard_code_colors()
    {
        var tokens = File.ReadAllText(PathOf("src", "AuditionModStudio.App", "DesignSystem", "ComponentTokens.xaml"));
        var components = File.ReadAllText(PathOf("src", "AuditionModStudio.App", "DesignSystem", "Components.xaml"));

        Assert.Contains("{ThemeResource AmsSurfaceColor}", tokens, StringComparison.Ordinal);
        Assert.Contains("ResourceKey=\"AmsControlPadding\"", tokens, StringComparison.Ordinal);
        Assert.DoesNotMatch(HexColorPattern(), components);
        Assert.DoesNotContain("Source=\"http", components, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Uri=\"http", components, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ImageSource=\"http", components, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reusable_components_cover_panels_cards_buttons_badges_and_typography()
    {
        var document = LoadDesignSystem("Components.xaml");
        var keys = ResourceKeys(document);

        Assert.Contains("AmsGamingPanelStyle", keys);
        Assert.Contains("AmsCardStyle", keys);
        Assert.Contains("AmsAccentCardStyle", keys);
        Assert.Contains("AmsPrimaryButtonStyle", keys);
        Assert.Contains("AmsSecondaryButtonStyle", keys);
        Assert.Contains("AmsStatusBadgeStyle", keys);
        Assert.Contains("AmsSectionTitleStyle", keys);
        Assert.Contains("AmsBodyTextStyle", keys);
        Assert.DoesNotContain(document.Descendants(), element =>
            element.Name.LocalName is "Frame" or "NavigationView" or "WebView2");
    }

    [Fact]
    public void Flat_button_template_has_clear_hover_press_disabled_and_keyboard_focus_states()
    {
        var document = LoadDesignSystem("Components.xaml");
        var stateNames = document.Descendants(Presentation + "VisualState")
            .Select(element => (string?)element.Attribute(Xaml + "Name"))
            .ToArray();
        var primaryStyle = document.Descendants(Presentation + "Style")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == "AmsPrimaryButtonStyle");

        Assert.Contains("Normal", stateNames);
        Assert.Contains("PointerOver", stateNames);
        Assert.Contains("Pressed", stateNames);
        Assert.Contains("Disabled", stateNames);
        Assert.Contains(document.Descendants(Presentation + "DoubleAnimation"), animation =>
            (string?)animation.Attribute("Storyboard.TargetProperty") == "Opacity");
        Assert.DoesNotContain(document.Descendants(Presentation + "DoubleAnimation"), animation =>
            (string?)animation.Attribute("Storyboard.TargetProperty") == "TranslateY");
        Assert.Contains("AmsFlatButtonTemplate", document.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("AmsDepthButtonTemplate", document.ToString(), StringComparison.Ordinal);
        Assert.Contains(primaryStyle.Descendants(Presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "UseSystemFocusVisuals"
            && (string?)setter.Attribute("Value") == "True");
        Assert.Contains(primaryStyle.Descendants(Presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "FocusVisualPrimaryBrush");
    }

    private static XDocument LoadDesignSystem(string fileName) =>
        Load("src", "AuditionModStudio.App", "DesignSystem", fileName);

    private static XDocument Load(params string[] segments) => XDocument.Load(PathOf(segments));

    private static string PathOf(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory.FullName, .. segments]);
    }

    private static string[] ResourceKeys(XContainer container)
    {
        var elements = container is XDocument document ? document.Root!.Elements() : container.Elements();
        return elements
            .Select(element => (string?)element.Attribute(Xaml + "Key"))
            .Where(key => key is not null)
            .Cast<string>()
            .ToArray();
    }

    [GeneratedRegex("#[0-9A-Fa-f]{6,8}")]
    private static partial Regex HexColorPattern();
}
