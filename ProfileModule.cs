using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;

namespace AlgorithmicQuestingProgression;

/// <summary>
/// ProfileModule — when config.OnlyZeroToHeroProfile is true, removes every launcher
/// profile template EXCEPT "SPT Zero to hero" on server start, so it's the only
/// selectable starting profile. GetProfileTemplates() returns the live
/// Dictionary&lt;string, ProfileSides&gt; keyed by profile name, so we just drop the
/// other keys. The on-disk profiles.json is never modified.
/// </summary>
public class ProfileModule(
    ISptLogger<AlgorithmicQuestingProgression> logger,
    DatabaseService databaseService)
{
    private const string Prefix = "[AQP][Profiles]";
    private const string Keep = "SPT Zero to hero";

    public void Run()
    {
        var profiles = databaseService.GetProfileTemplates();
        if (profiles == null || profiles.Count == 0)
        {
            logger.Warning($"{Prefix} no profile templates found; nothing to do.");
            return;
        }

        if (!profiles.Keys.Any(k => string.Equals(k, Keep, StringComparison.OrdinalIgnoreCase)))
        {
            logger.Warning($"{Prefix} '{Keep}' profile not found — leaving profiles untouched to avoid removing all.");
            return;
        }

        var toRemove = profiles.Keys
            .Where(k => !string.Equals(k, Keep, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in toRemove)
            profiles.Remove(key);

        logger.Success($"{Prefix} kept '{Keep}', removed {toRemove.Count} other profile template(s).");
    }
}
