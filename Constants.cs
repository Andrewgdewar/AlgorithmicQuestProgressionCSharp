namespace AlgorithmicQuestingProgression;

/// <summary>
/// Static constants ported from the old TS mod's <c>Constants.ts</c>.
/// </summary>
public static class Constants
{
    /// <summary>
    /// Quests to delete outright — deprecated / problematic / cross-trader-branching.
    /// Carried over from the old mod's removeList. The 3 "To Great Heights - Part 1/2/3"
    /// entries were dropped (they are now Arena PvP-mode quests handled by the RefModule).
    /// Names are re-validated against the live DB at runtime; misses are logged, not fatal.
    /// </summary>
    public static readonly HashSet<string> RemoveList =
    [
        "Make Amends",
        "Illegal Logging",
        "Chilly",
        "Enough Drinks for That One",
        "Hide in Plain Sight",
        "This Is My Party",
        "A Healthy Alternative",
        "Kind of Sabotage",
        "The Stylish One",
        "Important Patient",
        "Bloodhounds",
        "Hint",
        "Failed Setup",
        "Hustle",
        "Tourists",
        "Cocktail Tasting",
        "Overseas Trust - Part 1",
        "Overseas Trust - Part 2",
        "The Punisher Harvest",
        "The Tarkov Mystery",
        "A Key to Salvation",
        "Import ontrol",
        "Whats Your Evidence",
        "Caught Red-Handed",
        "Gunsmith - Special Order",
        "Gun Connoisseur",
        "Customer Communication",
        "Supply and Demand",
        "Into the Inferno",
        "In and Out",
        "Ours by Right",
        "Provide Cover",
        "Cream of the Crop",
        "Before the Rain",
        "Night of The Cult",
        "The Graven Image",
        "Dont Believe Your Eyes",
        "Dirty Blood",
        "Burn It Down",
        "The Root Cause",
        "Matter of Technique",
        "Find the Source",
        "Gloves Off",
        "Sample IV - A New Hope",
        "Darkest Hour Is Just Before Dawn",
        "Radical Treatment",
        "Forgotten Oaths",
        "Global Threat",
        "Watch the Watcher",
        "Not a Step Back",
        "Pressured by Circumstances",
        "Conservation Area",
        "Contagious Beast",
    ];
}
