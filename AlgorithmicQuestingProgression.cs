using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;

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
    ModHelper modHelper) : IOnLoad
{
    private const string Prefix = "[AlgorithmicQuestingProgression]";

    public Task OnLoad()
    {
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var config = modHelper.GetJsonDataFromFile<ModConfig>(pathToMod, "config/config.json");

        if (config.EnableOverhaulModule)
        {
            logger.Debug($"{Prefix} Overhaul module enabled (not yet implemented)");
            // TODO: OverhaulModule.Run(...)
        }

        if (config.EnableAdjusterModule)
        {
            logger.Debug($"{Prefix} Adjuster module enabled (not yet implemented)");
            // TODO: AdjusterModule.Run(...)
        }

        if (config.RemoveTransitQuests)
        {
            logger.Debug($"{Prefix} Transit removal enabled (not yet implemented)");
            // TODO: TransitModule.Run(...)
        }

        if (config.RefChanges)
        {
            logger.Debug($"{Prefix} Ref quest tweaks enabled (not yet implemented)");
            // TODO: RefModule.Run(...)
        }

        logger.Success(
            $"{Prefix} Loaded. Overhaul: {config.EnableOverhaulModule}, Adjuster: {config.EnableAdjusterModule}, " +
            $"Transits: {config.RemoveTransitQuests}, Ref: {config.RefChanges}");

        return Task.CompletedTask;
    }
}
