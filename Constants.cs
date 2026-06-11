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

        // --- Currency-handover quests ("Hand over RUB/EUR/USD x∞") — removed per curation rules.
        // (Spa Tour - Part 6 is also currency but stays: it is internal to the Spa Tour chain.)
        "An Apple a Day Keeps the Doctor Away",
        "Fair Price - Part 1",
        "Friend From the West - Part 2",
        "Friend from Norvinsk - Alternative Solution",
        "Loyalty Buyout",
        "Make Amends - Buyout",
        "Mentor",

        // --- Loyalty/standing-gate quests ("Reach level N loyalty / X standing") — blocking, removed.
        // (Insider is also a loyalty gate but stays: it is internal to the Insider/Network Provider chain.)
        "Establish Contact",
        "No Fuss Needed",
        "Only Business",
        "Perfect Mediator",

        // --- Christmas / seasonal-event quests not caught by the eventQuests filter.
        "The Price of Celebration",

        // --- Pure skill-leveling / XP-grind quests ("Reach the required <skill> skill level"
        // as the ONLY objective). Standalone ones removed here; chain-internal skill
        // conditions (Wet Job - Part 6, Health Care Privacy - Part 4, Signal - Part 4,
        // The Survivalist Path - Combat Medic) are stripped at runtime instead of deleted.
        "Athlete",
        "Flint",
        "Scavenger",
        "Charisma Brings Success",
    ];
}
