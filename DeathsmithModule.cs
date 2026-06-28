using System.Text.RegularExpressions;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils.Cloners;

namespace AlgorithmicQuestingProgression;

/// <summary>
/// DeathsmithModule — keeps the vanilla Gunsmith questline intact and adds a parallel
/// "Deathsmith - Part N" kill-questline for the same weapons, forked against it.
///
/// Runs AFTER OverhaulModule (chains already rebuilt) and AdjusterModule (rewards already
/// scaled), so each Deathsmith clone inherits its Gunsmith twin's final reward set.
///
/// For every quest named "Gunsmith - Part N" that is still a WeaponAssembly quest:
///   1. Clone it into "Deathsmith - Part N" with a deterministic new id.
///   2. Replace the build objective with an "eliminate N enemies with &lt;weapon&gt;"
///      CounterCreator (same formula as the replaceGunsmith feature).
///   3. Fork the pair: each quest gets a Fail condition that fires when its twin succeeds,
///      and both are made non-restartable (no retry).
///   4. The Deathsmith shares the Gunsmith's start gate, so both are offered together.
///   5. Relax every downstream start gate that pointed at a forked Gunsmith quest so it
///      also accepts the Fail state — the next stage then unlocks once EITHER path is
///      resolved (winner = Success, loser = Fail).
/// </summary>
public class DeathsmithModule(
    ISptLogger<AlgorithmicQuestingProgression> logger,
    DatabaseService databaseService,
    ModConfig config,
    Dictionary<string, LocaleTemplate> localeConfig,
    ICloner cloner)
{
    private const string Prefix = "[AQP][Deathsmith]";
    private const string ArenaTraderId = "6617beeaa9cfa777ca915b7c";

    private static readonly Regex GunsmithPartRe = new(@"^Gunsmith - Part (\d+)$", RegexOptions.Compiled);

    // Collected locale rewrites: (questId, number, weaponTpl, objectiveId, killValue).
    private record LocaleRewrite(string QuestId, int Number, string WeaponTpl, string ObjectiveId, int Value);

    public void Run()
    {
        var quests = databaseService.GetQuests();

        var rewrites = new List<LocaleRewrite>();
        var forkedGunsmithIds = new HashSet<string>();
        var created = 0;

        // ---- pass 1: clone each Gunsmith part into a forked Deathsmith twin ----
        foreach (var (id, gunsmith) in quests.ToList())
        {
            if (gunsmith.TraderId == new MongoId(ArenaTraderId)) continue;

            var match = GunsmithPartRe.Match(gunsmith.QuestName ?? "");
            if (!match.Success) continue;
            if (gunsmith.Type != QuestTypeEnum.WeaponAssembly) continue;

            var finish = gunsmith.Conditions?.AvailableForFinish;
            if (finish == null || finish.Count == 0) continue;

            var weaponTpl = FirstTarget(finish[0]);
            if (string.IsNullOrEmpty(weaponTpl)) continue;

            var number = int.Parse(match.Groups[1].Value);
            var gunsmithId = id.ToString();
            var newId = Utils.GenerateMongoIdFromSeed("deathsmith" + gunsmithId);

            var death = cloner.Clone(gunsmith);
            if (death?.Conditions == null) continue;

            // identity
            death.Id = new MongoId(newId);
            death.QuestName = $"Deathsmith - Part {number}";
            death.Name = $"{newId} name";
            death.Description = $"{newId} description";
            death.Type = QuestTypeEnum.Elimination;
            death.Restartable = false;

            // kill objective (distinct seeds from the replaceGunsmith ids via "deathsmith" suffix)
            var killCond = Utils.GunsmithKillCondition(
                number, weaponTpl, config.BaseKillCountQuantity, config.KillCountModifier, "deathsmith");
            death.Conditions.AvailableForFinish = [killCond];

            // re-id the cloned start gate so it doesn't share condition ids with its twin,
            // while keeping the same target/status (chain rebuild already gated the twin).
            if (death.Conditions.AvailableForStart != null)
                foreach (var c in death.Conditions.AvailableForStart)
                    c.Id = new MongoId(Utils.GenerateMongoIdFromSeed(newId + c.Id + "start"));

            // fork: completing one fails the other (no retry)
            death.Conditions.Fail = [Utils.FailOnQuestSuccess(gunsmithId, newId + "failOnGunsmith")];
            gunsmith.Restartable = false;
            gunsmith.Conditions!.Fail = [Utils.FailOnQuestSuccess(newId, gunsmithId + "failOnDeathsmith")];

            quests[new MongoId(newId)] = death;
            forkedGunsmithIds.Add(gunsmithId);
            rewrites.Add(new LocaleRewrite(newId, number, weaponTpl, killCond.Id.ToString(), (int)(killCond.Value ?? 1)));
            created++;
        }

        if (created == 0)
        {
            logger.Warning($"{Prefix} no Gunsmith parts found to fork (was replaceGunsmith run first?).");
            return;
        }

        // ---- pass 2: relax downstream start gates that depend on a forked Gunsmith part ----
        // so the next stage unlocks whether the player took the Gunsmith (Success) or the
        // Deathsmith (Gunsmith twin -> Fail) path.
        var relaxed = 0;
        foreach (var (_, quest) in quests)
        {
            var start = quest.Conditions?.AvailableForStart;
            if (start == null) continue;
            foreach (var c in start)
            {
                if (c.ConditionType != "Quest") continue;
                var target = c.Target == null ? null : c.Target.IsList ? c.Target.List?.FirstOrDefault() : c.Target.Item;
                if (target == null || !forkedGunsmithIds.Contains(target)) continue;
                c.Status ??= [];
                if (c.Status.Add(QuestStatusEnum.Fail)) relaxed++;
            }
        }

        ApplyDeathsmithLocale(rewrites);

        logger.Success($"{Prefix} created {created} Deathsmith quests, relaxed {relaxed} downstream start gates.");
    }

    /// <summary>
    /// Register a transformer on each language's locale writing the Deathsmith title,
    /// description and objective strings, substituting the weapon's localized name.
    /// Mirrors AdjusterModule.ApplyGunsmithLocale.
    /// </summary>
    private void ApplyDeathsmithLocale(List<LocaleRewrite> rewrites)
    {
        var locales = databaseService.GetTables().Locales?.Global;
        if (locales == null) return;

        foreach (var (langKey, lazy) in locales)
        {
            var template = localeConfig.TryGetValue(langKey, out var t) ? t
                : localeConfig.GetValueOrDefault("en") ?? new LocaleTemplate();
            var nameTemplate = template.DeathsmithName;
            var descTemplate = template.Description;
            var taskTemplate = template.Task;

            lazy.AddTransformer(data =>
            {
                foreach (var r in rewrites)
                {
                    var name = data.GetValueOrDefault($"{r.WeaponTpl} Name")
                               ?? data.GetValueOrDefault($"{r.WeaponTpl} ShortName") ?? "weapon";
                    var shortName = data.GetValueOrDefault($"{r.WeaponTpl} ShortName")
                                    ?? data.GetValueOrDefault($"{r.WeaponTpl} Name") ?? "weapon";

                    data[$"{r.QuestId} name"] = nameTemplate.Replace("<number>", r.Number.ToString());
                    data[$"{r.QuestId} description"] = descTemplate.Replace("<weapon>", name);
                    data[r.ObjectiveId] = taskTemplate.Replace("<weapon>", shortName).Replace("<number>", r.Value.ToString());
                }
                return data;
            });
        }
    }

    /// <summary>Extract the first template id from a condition's target (single or list).</summary>
    private static string? FirstTarget(QuestCondition condition)
    {
        var target = condition.Target;
        if (target == null) return null;
        if (target.IsList) return target.List?.FirstOrDefault();
        return target.Item;
    }
}
