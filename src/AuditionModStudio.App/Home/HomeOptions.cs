using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;

namespace AuditionModStudio.App.Home;

public sealed record HomeGameOption(GameId GameId, string DisplayName);

public sealed record HomeModOption(
    ModId ModId,
    string DisplayName,
    string Category,
    string Description,
    string CompatibilityInformation);
