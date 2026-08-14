namespace AuditionModStudio.Core.Games;

public sealed class GameDefinition
{
    public const int MaximumDisplayNameLength = 128;

    public GameDefinition(GameId id, string displayName)
    {
        if (!id.IsValid)
        {
            throw new ArgumentException("Game definition requires a valid stable ID.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (displayName.Length > MaximumDisplayNameLength
            || displayName.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Game display name must contain at most 128 characters and no control characters.",
                nameof(displayName));
        }

        Id = id;
        DisplayName = displayName;
    }

    public GameId Id { get; }

    public string DisplayName { get; }
}
