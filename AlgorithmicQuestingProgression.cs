using System.Reflection;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;

namespace AlgorithmicQuestingProgression;

/// <summary>
/// Mod metadata — the SPT 4.0 replacement for the old package.json.
/// Carried over from the original TypeScript mod (DewardianDev, MIT), bumped to v2.0.0
/// for the C# rewrite.
/// </summary>
public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.dewardiandev.algorithmicquestingprogression";
    public override string Name { get; init; } = "AlgorithmicQuestingProgression";
    public override string Author { get; init; } = "DewardianDev";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } = new("2.0.0");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.0");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; } = "https://github.com/Andrewgdewar/AlgorithmicQuestingProgression";
    public override bool? IsBundleMod { get; init; } = false;
    public override string License { get; init; } = "MIT";
}

/// <summary>
/// Entry point. Loads config and dispatches each enabled module after the database is loaded.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 1)]
public class AlgorithmicQuestingProgression(
    ISptLogger<AlgorithmicQuestingProgression> logger,
    ModHelper modHelper,
    DatabaseService databaseService,
    ConfigServer configServer) : IOnLoad
{
    private const string Prefix = "[AlgorithmicQuestingProgression]";

    public Task OnLoad()
    {
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var config = modHelper.GetJsonDataFromFile<ModConfig>(pathToMod, "config/config.json");
        var mainQuests = modHelper.GetJsonDataFromFile<Dictionary<string, List<JsonElement>>>(pathToMod, "config/MainQuests.json");
        var adjustments = modHelper.GetJsonDataFromFile<QuestAdjustments>(pathToMod, "config/questAdjustments.json");
        var ammoUnlocks = modHelper.GetJsonDataFromFile<Dictionary<string, List<AmmoUnlock>>>(pathToMod, "config/ammoLevelUnlocks.json");

        if (config.EnableOverhaulModule)
        {
            new OverhaulModule(logger, databaseService, configServer, config, mainQuests, adjustments, ammoUnlocks).Run();
        }

        if (config.EnableAdjusterModule)
        {
            var localeConfig = modHelper.GetJsonDataFromFile<Dictionary<string, LocaleTemplate>>(pathToMod, "config/localeConfig.json");
            new AdjusterModule(logger, databaseService, config, localeConfig).Run();
        }

        if (config.RemoveTransitQuests)
        {
            logger.Debug($"{Prefix} Transit removal handled inside Overhaul teardown for now");
            // NOTE: transit stripping currently runs inside OverhaulModule; standalone TransitModule TBD
        }

        if (config.RefChanges)
        {
            logger.Debug($"{Prefix} Ref/Arena quests are delegated to Lacy's PvE Tweaks (refChanges) — AQP does not touch them.");
        }

        if (config.OnlyZeroToHeroProfile)
        {
            new ProfileModule(logger, databaseService).Run();
        }

        logger.Success(
            $"{Prefix} Loaded. Overhaul: {config.EnableOverhaulModule}, Adjuster: {config.EnableAdjusterModule}, " +
            $"Transits: {config.RemoveTransitQuests}, Ref: {config.RefChanges}");

        return Task.CompletedTask;
    }
}
