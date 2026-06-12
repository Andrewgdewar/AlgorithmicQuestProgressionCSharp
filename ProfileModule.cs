using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;

namespace AlgorithmicQuestingProgression;

/// <summary>
/// ProfileModule — when config.OnlyZeroToHeroProfile is true, removes every launcher
/// profile template EXCEPT "SPT Zero to hero" on server start, then customizes that
/// profile's starting kit (remove alpha secured container + knife, grant Roubles).
/// GetProfileTemplates() returns the live Dictionary&lt;string, ProfileSides&gt; keyed by
/// profile name; the on-disk profiles.json is never modified.
/// </summary>
public class ProfileModule(
    ISptLogger<AlgorithmicQuestingProgression> logger,
    DatabaseService databaseService,
    ModConfig config)
{
    private const string Prefix = "[AQP][Profiles]";
    private const string Keep = "SPT Zero to hero";
    private const string RoublesTpl = "5449016a4bdc2d6f028b456f";

    public void Run()
    {
        var profiles = databaseService.GetProfileTemplates();
        if (profiles == null || profiles.Count == 0)
        {
            logger.Warning($"{Prefix} no profile templates found; nothing to do.");
            return;
        }

        var keepKey = profiles.Keys.FirstOrDefault(k => string.Equals(k, Keep, StringComparison.OrdinalIgnoreCase));
        if (keepKey == null)
        {
            logger.Warning($"{Prefix} '{Keep}' profile not found — leaving profiles untouched to avoid removing all.");
            return;
        }

        var toRemove = profiles.Keys.Where(k => k != keepKey).ToList();
        foreach (var key in toRemove)
            profiles.Remove(key);

        logger.Success($"{Prefix} kept '{Keep}', removed {toRemove.Count} other profile template(s).");

        CustomizeStartingKit(profiles[keepKey]);
    }

    /// <summary>Apply the kit tweaks to both the bear and usec sides of the kept profile.</summary>
    private void CustomizeStartingKit(ProfileSides sides)
    {
        foreach (var side in new[] { sides.Bear, sides.Usec })
        {
            var inventory = side?.Character?.Inventory;
            var items = inventory?.Items;
            if (items == null) continue;

            if (config.ZeroToHeroRemoveSecuredContainer)
                items.RemoveAll(i => string.Equals(i.SlotId, "SecuredContainer", StringComparison.OrdinalIgnoreCase));

            if (config.ZeroToHeroRemoveKnife)
                items.RemoveAll(i => string.Equals(i.SlotId, "Scabbard", StringComparison.OrdinalIgnoreCase));

            if (config.ZeroToHeroStartingRoubles > 0 && inventory!.Stash is { } stashId)
            {
                items.Add(new Item
                {
                    Id = new MongoId(Utils.GenerateMongoIdFromSeed($"{Keep}{stashId}roubles")),
                    Template = new MongoId(RoublesTpl),
                    ParentId = stashId,
                    SlotId = "hideout",
                    Location = new ItemLocation { X = 0, Y = 0, R = ItemRotation.Horizontal, IsSearched = true },
                    Upd = new Upd { StackObjectsCount = config.ZeroToHeroStartingRoubles },
                });
            }
        }

        logger.Debug(
            $"{Prefix} customized '{Keep}' kit (removeContainer={config.ZeroToHeroRemoveSecuredContainer}, " +
            $"removeKnife={config.ZeroToHeroRemoveKnife}, roubles={config.ZeroToHeroStartingRoubles}).");
    }
}
