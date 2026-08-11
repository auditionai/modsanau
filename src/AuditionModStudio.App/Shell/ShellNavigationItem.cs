namespace AuditionModStudio.App.Shell;

public sealed record ShellNavigationItem(
    AppRoute Route,
    string Label,
    string Glyph,
    string Title,
    string Description);
