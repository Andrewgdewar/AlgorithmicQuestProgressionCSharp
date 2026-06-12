using System.Text.Json;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;

namespace AlgorithmicQuestingProgression;

/// <summary>
/// AdjusterModule — the scalar-modifier pass (port of the old AdjusterModule.ts).
/// Applies global multipliers to quest objectives and rewards, and (optionally)
/// converts every Gunsmith WeaponAssembly quest into an "eliminate N enemies with
/// &lt;weapon&gt;" kill quest (config.ReplaceGunsmith).
/// </summary>
public class AdjusterModule(
    ISptLogger<AlgorithmicQuestingProgression> logger,
    DatabaseService databaseService,
    ModConfig config,
    Dictionary<string, LocaleTemplate> localeConfig)
{
    private const string Prefix = "[AQP][Adjuster]";

    // Arena trader (Lacy's "Ref") — owned by Lacy's PvE Tweaks; AQP skips its quests.
    private const string ArenaTraderId = "6617beeaa9cfa777ca915b7c";

    public void Run()
    {
        var quests = databaseService.GetQuests();
        var items = databaseService.GetItems();

        // Collected gunsmith locale rewrites: (descriptionId, taskId, weaponTpl, killValue).
        var gunsmithLocale = new List<(string descId, string taskId, string weaponTpl, int value)>();

        var plantAdjusted = 0;
        var findAdjusted = 0;
        var killAdjusted = 0;
        var gunsmithReplaced = 0;
        var rewardsAdjusted = 0;

        foreach (var (id, quest) in quests)
        {
            // Arena/Ref quests are owned by Lacy's PvE Tweaks — never touch them.
            if (quest.TraderId == ArenaTraderId) continue;

            var conds = quest.Conditions;

            // --- level start-requirement scaling ---
            if (config.QuestLevelUnlockModifier != 1 && conds?.AvailableForStart != null)
            {
                foreach (var c in conds.AvailableForStart)
                {
                    if (c.ConditionType != "Level") continue;
                    var v = c.Value ?? 1;
                    c.Value = Math.Max(1, Math.Round(v * config.QuestLevelUnlockModifier));
                }
            }

            // --- finish-objective scaling ---
            var finish = conds?.AvailableForFinish;
            if (finish != null)
            {
                foreach (var c in finish)
                {
                    switch (c.ConditionType)
                    {
                        case "LeaveItemAtLocation":
                        case "PlaceBeacon":
                            if (config.PlantTimeModifier == 1) break;
                            if (c.PlantTime is { } pt)
                            {
                                c.PlantTime = Math.Max(1, (int)Math.Round(pt * config.PlantTimeModifier));
                                plantAdjusted++;
                            }
                            break;

                        case "CounterCreator":
                            if ((c.Value ?? 0) == 1 || config.KillQuestCountModifier == 1) break;
                            c.Value = Math.Max(1, Math.Round((c.Value ?? 0) * config.KillQuestCountModifier));
                            killAdjusted++;
                            break;

                        case "HandoverItem":
                        case "FindItem":
                            if (config.FindItemQuestModifier == 1) break;
                            var tpl = FirstTarget(c);
                            if (tpl != null && items.TryGetValue(new MongoId(tpl), out var item) &&
                                item.Properties?.QuestItem == true)
                                break; // quest items keep their count (usually 1)
                            c.Value = Math.Max(1, Math.Round((c.Value ?? 0) * config.FindItemQuestModifier));
                            findAdjusted++;
                            break;
                    }
                }
            }

            // --- gunsmith replacement ---
            if (config.ReplaceGunsmith && quest.Type == QuestTypeEnum.WeaponAssembly &&
                finish is { Count: > 0 })
            {
                var weaponTpl = FirstTarget(finish[0]);
                if (!string.IsNullOrEmpty(weaponTpl))
                {
                    var number = Utils.GetNumbersFromName(quest.QuestName ?? "");
                    var killCond = Utils.GunsmithKillCondition(number, weaponTpl,
                        config.BaseKillCountQuantity, config.KillCountModifier);
                    var taskId = killCond.Id.ToString();
                    var descId = Utils.GenerateMongoIdFromSeed(taskId + "descriptionId");

                    quest.Type = QuestTypeEnum.Elimination;
                    quest.Description = descId;
                    quest.Conditions!.AvailableForFinish = [killCond];

                    gunsmithLocale.Add((descId, taskId, weaponTpl, (int)(killCond.Value ?? 1)));
                    gunsmithReplaced++;
                }
            }

            // --- reward scaling ---
            if (quest.Rewards != null && quest.Rewards.TryGetValue("Success", out var success) && success != null)
            {
                foreach (var rew in success)
                {
                    switch (rew.Type)
                    {
                        case RewardType.Experience:
                            if (config.QuestExperienceModifier == 1) break;
                            if ((rew.Value ?? 0) > 0)
                            {
                                rew.Value = Math.Max(1, Math.Round((rew.Value ?? 0) * config.QuestExperienceModifier));
                                rewardsAdjusted++;
                            }
                            break;

                        case RewardType.Item:
                            if (config.ItemRewardModifier == 1) break;
                            var first = rew.Items?.FirstOrDefault();
                            if (rew.Items is { Count: > 1 } || first == null) break;
                            if ((rew.Value ?? 0) == 1) break;
                            rew.Value = Math.Max(1, Math.Round((rew.Value ?? 0) * config.ItemRewardModifier));
                            if (first.Upd is { } upd)
                                upd.StackObjectsCount = Math.Max(1, Math.Round((upd.StackObjectsCount ?? 1) * config.ItemRewardModifier));
                            rewardsAdjusted++;
                            break;

                        case RewardType.TraderStanding:
                            if (config.TraderStandingRewardModifier == 1) break;
                            rew.Value = Math.Round((rew.Value ?? 0) / 0.05 * config.TraderStandingRewardModifier * 0.05 * 100) / 100;
                            rewardsAdjusted++;
                            break;
                    }
                }
            }
        }

        // --- apply gunsmith locale rewrites (lazy, via transformers per language) ---
        if (gunsmithLocale.Count > 0)
            ApplyGunsmithLocale(gunsmithLocale);

        logger.Debug(
            $"{Prefix} done. plantTime: {plantAdjusted}, find/handover: {findAdjusted}, kill: {killAdjusted} adjusted. " +
            $"Gunsmith replaced: {gunsmithReplaced}. Rewards adjusted: {rewardsAdjusted}.");
    }

    /// <summary>
    /// Register a transformer on each language's locale that writes the gunsmith quest
    /// description + task strings, substituting the weapon's localized name/short-name
    /// (read from the same locale dictionary at load time).
    /// </summary>
    private void ApplyGunsmithLocale(List<(string descId, string taskId, string weaponTpl, int value)> rewrites)
    {
        var locales = databaseService.GetTables().Locales?.Global;
        if (locales == null) return;

        foreach (var (langKey, lazy) in locales)
        {
            var template = localeConfig.TryGetValue(langKey, out var t) ? t
                : localeConfig.GetValueOrDefault("en") ?? new LocaleTemplate();
            var descTemplate = template.Description;
            var taskTemplate = template.Task;

            lazy.AddTransformer(data =>
            {
                foreach (var (descId, taskId, weaponTpl, value) in rewrites)
                {
                    var name = data.GetValueOrDefault($"{weaponTpl} Name")
                               ?? data.GetValueOrDefault($"{weaponTpl} ShortName") ?? "weapon";
                    var shortName = data.GetValueOrDefault($"{weaponTpl} ShortName")
                                    ?? data.GetValueOrDefault($"{weaponTpl} Name") ?? "weapon";

                    data[descId] = descTemplate.Replace("<weapon>", name);
                    data[taskId] = taskTemplate.Replace("<weapon>", shortName).Replace("<number>", value.ToString());
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

/// <summary>One language's gunsmith description/task templates from localeConfig.json.</summary>
public record LocaleTemplate
{
    [System.Text.Json.Serialization.JsonPropertyName("description")]
    public string Description { get; set; } = "Demonstrate your expertise with a <weapon>.";

    [System.Text.Json.Serialization.JsonPropertyName("task")]
    public string Task { get; set; } = "Eliminate <number> enemies using a <weapon>.";
}
