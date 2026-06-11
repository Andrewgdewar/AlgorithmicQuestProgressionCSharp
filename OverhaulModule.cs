using System.Text.Json;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;

namespace AlgorithmicQuestingProgression;

/// <summary>
/// OverhaulModule — phase 1 "teardown" + phase 2a "applier".
///
/// Teardown:
///   1. REMOVE quests: removeList + seasonal-event quests + event-enemy quests.
///   2. TRANSIT: drop pure "transit between maps" finish conditions / quests.
///   3. LEVELS: strip every Level start-requirement.
///   4. DISASSOCIATE: clear cross-quest links + flat-remove ALL failure penalties.
///
/// Applier (phase 2a — consumes config/questAdjustments.json + MainQuests.json):
///   5. Hide every quest NOT in the curated list behind a level-99 gate.
///   6. Clear AvailableForStart on curated quests (chain rebuild repopulates later).
///   7. deleteReqList — remove specific AvailableForFinish conditions by id.
///   8. adjustReqsList — merge value/oneSessionOnly onto conditions by id.
///   9. weaponBuildStripList — clear weapon-mod constraints on Kills conditions.
///  10. skillConditionStripList — remove Skill-type finish conditions.
/// </summary>
public class OverhaulModule(
    ISptLogger<AlgorithmicQuestingProgression> logger,
    DatabaseService databaseService,
    ConfigServer configServer,
    ModConfig config,
    Dictionary<string, List<JsonElement>> mainQuests,
    QuestAdjustments adjustments,
    Dictionary<string, List<AmmoUnlock>> ammoUnlocks)
{
    private const string Prefix = "[AQP][Overhaul]";

    // Arena trader (Lacy's "Ref") — its PvP-mode quests are rewritten by the RefModule, not removed.
    private const string ArenaTraderId = "6617beeaa9cfa777ca915b7c";

    // Enemy roles that only spawn during events / special modes (zombies, the Halloween
    // cultist bosses, the arena Bloodhound). Quests requiring these can't be done in a
    // normal raid, so they are removed.
    private static readonly HashSet<string> EventOnlyRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "infectedAssault", "infectedTagilla", "infectedCivil", "infectedLaborant", "infectedPmc",
        "sectantOni", "sectantPrizrak", "sectantPredvestnik", "arenaFighterEvent",
    };

    public void Run()
    {
        var quests = databaseService.GetQuests();

        // Seasonal event quest ids (require a calendar event window -> remove).
        var seasonalEventIds = GetSeasonalEventQuestIds();

        var removedRemoveList = 0;
        var removedEvent = 0;
        var removedEventEnemy = 0;
        var removedPureTransit = 0;
        var notFoundFromRemoveList = new HashSet<string>(Constants.RemoveList);

        // ---- Pass 1: collect ids to remove ----
        var toRemove = new List<string>();
        foreach (var (id, quest) in quests)
        {
            var name = quest.QuestName ?? "";

            if (Constants.RemoveList.Contains(name))
            {
                toRemove.Add(id);
                notFoundFromRemoveList.Remove(name);
                removedRemoveList++;
                continue;
            }

            if (seasonalEventIds.Contains(id))
            {
                toRemove.Add(id);
                removedEvent++;
                continue;
            }

            if (RequiresEventEnemy(quest))
            {
                toRemove.Add(id);
                removedEventEnemy++;
                continue;
            }
        }

        foreach (var id in toRemove)
            quests.Remove(id);

        if (config.OverHaulDebug && notFoundFromRemoveList.Count > 0)
            logger.Warning($"{Prefix} removeList names not found in DB: {string.Join(", ", notFoundFromRemoveList)}");

        // ---- Pass 2-4: per remaining quest ----
        var levelsStripped = 0;
        var transitStripped = 0;
        var crossLinksStripped = 0;
        foreach (var (id, quest) in quests.ToList())
        {
            var conds = quest.Conditions;
            if (conds == null) continue;

            var finish = conds.AvailableForFinish;

            // --- transit: drop finish conditions that are a pure "Transit" objective ---
            if (finish != null)
            {
                var transitConds = finish.Where(IsRealTransitCondition).ToList();
                if (transitConds.Count > 0)
                {
                    if (transitConds.Count == finish.Count)
                    {
                        // Quest is ONLY transit objectives -> remove the quest entirely.
                        quests.Remove(id);
                        removedPureTransit++;
                        continue;
                    }

                    var transitIds = transitConds.Select(c => c.Id.ToString()).ToHashSet();
                    foreach (var tc in transitConds)
                        finish.Remove(tc);
                    transitStripped += transitConds.Count;

                    // Fix dangling VisibilityConditions that pointed at a removed transit step.
                    foreach (var c in finish)
                    {
                        var vis = c.VisibilityConditions;
                        if (vis == null) continue;
                        vis.RemoveAll(v => v?.Target != null && transitIds.Contains(v.Target.ToString()!));
                    }
                }
            }

            // --- levels: strip ALL Level start-requirements ---
            if (conds.AvailableForStart != null)
            {
                var before = conds.AvailableForStart.Count;
                conds.AvailableForStart.RemoveAll(c => c.ConditionType == "Level");
                levelsStripped += before - conds.AvailableForStart.Count;
            }

            // --- flat failure-penalty removal: EVERY quest, always ---
            // Clear ALL Fail conditions + Fail rewards, make restartable (no fail-lockouts).
            conds.Fail?.Clear();
            if (quest.Rewards != null && quest.Rewards.TryGetValue("Fail", out var failRewards))
                failRewards?.Clear();
            quest.Restartable = true;

            // --- disassociate: strip cross-quest links so the chain can be rebuilt cleanly ---
            // Arena PvP quests are left intact for the RefModule to rewrite.
            if (quest.TraderId != ArenaTraderId)
            {
                crossLinksStripped += conds.AvailableForStart?.RemoveAll(c => c.ConditionType == "Quest") ?? 0;
                crossLinksStripped += finish?.RemoveAll(c => c.ConditionType == "Quest") ?? 0;
            }
        }

        logger.Success(
            $"{Prefix} teardown done. Removed: {removedRemoveList} (removeList), {removedEvent} (seasonal), " +
            $"{removedEventEnemy} (event-enemy), {removedPureTransit} (pure-transit). " +
            $"Stripped: {levelsStripped} level reqs, {transitStripped} transit conditions, {crossLinksStripped} cross-quest links. " +
            $"Quests remaining: {quests.Count}");

        // ---- Phase 2a: applier (consumes MainQuests + questAdjustments) ----
        ApplyAdjustments(quests);

        // ---- Phase 2b: chain rebuild + trader unlocks + fence ----
        RebuildChains(quests);

        // ---- Phase 2c: reward rebalance + container rewards ----
        RebalanceRewards(quests);

        // ---- Phase 2d: trader tweaks (faction gates, dailies, ammo loyalty levels) ----
        ApplyTraderTweaks();

        // ---- Phase 2e: assort-unlock reassignment ----
        ReassignAssorts(quests);
    }

    /// <summary>
    /// Phase 2a applier. Hides non-curated quests, clears curated quests' start
    /// requirements (the chain rebuild repopulates them later), and applies the
    /// per-quest objective edits from questAdjustments.json.
    /// </summary>
    private void ApplyAdjustments(Dictionary<MongoId, Quest> quests)
    {
        // name -> quest (first occurrence wins on duplicate names)
        var byName = new Dictionary<string, Quest>();
        foreach (var quest in quests.Values)
        {
            var name = quest.QuestName ?? "";
            if (name.Length > 0 && !byName.ContainsKey(name))
                byName[name] = quest;
        }

        // Curated quest names (flattened, including grouped chain members).
        var usedNames = FlattenMainQuests().ToHashSet();

        var missing = usedNames.Where(n => !byName.ContainsKey(n)).ToList();
        if (missing.Count > 0)
            logger.Warning($"{Prefix} {missing.Count} curated quest(s) not found in DB: {string.Join(", ", missing)}");

        var hidden = 0;
        var deleted = 0;
        var adjusted = 0;
        var weaponStripped = 0;
        var skillStripped = 0;

        foreach (var (id, quest) in quests)
        {
            // Arena quests are owned by the RefModule — leave them alone.
            if (quest.TraderId == ArenaTraderId) continue;

            var conds = quest.Conditions;
            if (conds == null) continue;

            var name = quest.QuestName ?? "";

            // --- hide quests not in the curated list behind a level-99 gate ---
            if (!usedNames.Contains(name))
            {
                conds.AvailableForStart = [Utils.AvailableForStartLevelRequirement(99, id.ToString() + "remove")];
                hidden++;
                continue;
            }

            // --- curated quest: clear start reqs (chain rebuild repopulates) ---
            conds.AvailableForStart = [];

            var finish = conds.AvailableForFinish;
            if (finish == null) continue;

            // --- deleteReqList: drop specific finish conditions by id ---
            if (adjustments.DeleteReqList.TryGetValue(name, out var deleteSpec) &&
                deleteSpec.TryGetValue("id", out var deleteIds) && deleteIds.Count > 0)
            {
                var idSet = deleteIds.ToHashSet();
                deleted += finish.RemoveAll(c => idSet.Contains(c.Id.ToString()));
            }

            // --- adjustReqsList: merge value / oneSessionOnly onto conditions by id ---
            if (adjustments.AdjustReqsList.TryGetValue(name, out var adjustSpec))
            {
                foreach (var c in finish)
                {
                    if (!adjustSpec.TryGetValue(c.Id.ToString(), out var fields)) continue;
                    if (fields.Value.HasValue) c.Value = fields.Value.Value;
                    if (fields.OneSessionOnly.HasValue) c.OneSessionOnly = fields.OneSessionOnly.Value;
                    adjusted++;
                }
            }

            // --- weaponBuildStripList: clear weapon-mod constraints on Kills conditions ---
            if (adjustments.WeaponBuildStripList.Contains(name))
            {
                foreach (var c in finish)
                {
                    var inner = c.Counter?.Conditions;
                    if (inner == null) continue;
                    foreach (var ic in inner)
                    {
                        if (ic.ConditionType != "Kills") continue;
                        ic.WeaponModsInclusive = new List<List<string>>();
                        ic.WeaponModsExclusive = new List<List<string>>();
                        weaponStripped++;
                    }
                }
            }

            // --- skillConditionStripList: remove Skill-type finish conditions ---
            if (adjustments.SkillConditionStripList.Contains(name))
                skillStripped += finish.RemoveAll(c => c.ConditionType == "Skill");
        }

        logger.Success(
            $"{Prefix} applier done. Hidden (non-curated @lvl99): {hidden}. " +
            $"deleteReqList: {deleted} conditions removed. adjustReqsList: {adjusted} conditions tuned. " +
            $"weaponBuildStrip: {weaponStripped} kills conditions. skillStrip: {skillStripped} conditions removed.");
    }

    /// <summary>
    /// Phase 2b. Rebuild the linear progression: for each trader, gate quests behind the
    /// previous N (TraderQuestProgressionQuantity); grouped chains are strict 1-by-1.
    /// Then wire trader unlocks and the Fence start requirement.
    /// </summary>
    private void RebuildChains(Dictionary<MongoId, Quest> quests)
    {
        // name -> id (first occurrence wins on duplicate names)
        var nameToId = new Dictionary<string, string>();
        foreach (var (id, quest) in quests)
        {
            var name = quest.QuestName ?? "";
            if (name.Length > 0 && !nameToId.ContainsKey(name))
                nameToId[name] = id.ToString();
        }

        string IdOrEmpty(string name) => nameToId.GetValueOrDefault(name) ?? "";

        // ---- chain rebuild per trader ----
        var chainsBuilt = 0;
        foreach (var (trader, list) in mainQuests)
        {
            var mainList = new List<string>();   // ids of first-of-group / standalone
            var groups = new List<List<string>>();

            foreach (var el in list)
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (string.IsNullOrEmpty(s)) continue;
                    mainList.Add(IdOrEmpty(s));
                }
                else if (el.ValueKind == JsonValueKind.Array)
                {
                    var group = el.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .Select(s => IdOrEmpty(s!))
                        .ToList();
                    if (group.Count == 0) continue;
                    groups.Add(group);
                    mainList.Add(group[0]);   // the chain's head sits on the main line
                }
            }

            var quantity = adjustments.TraderQuestProgressionQuantity.GetValueOrDefault(trader, 1);
            Utils.IterateOverArrayAddingQuestReqs(quests, mainList, quantity);

            // within each grouped chain: strict linear (1-by-1)
            foreach (var group in groups)
                Utils.IterateOverArrayAddingQuestReqs(quests, group, 1);

            chainsBuilt++;
        }

        // ---- trader unlocks ----
        var traders = databaseService.GetTables().Traders;
        var unlocksWired = 0;
        foreach (var (traderName, unlockQuestName) in adjustments.TraderUnlockQuests)
        {
            if (!Constants.TraderIds.TryGetValue(traderName, out var traderId)) continue;
            if (traders == null || !traders.TryGetValue(new MongoId(traderId), out var trader) || trader?.Base == null)
                continue;

            if (string.IsNullOrEmpty(unlockQuestName))
            {
                trader.Base.UnlockedByDefault = true;
                continue;
            }

            trader.Base.UnlockedByDefault = false;
            if (nameToId.TryGetValue(unlockQuestName, out var qid) &&
                quests.TryGetValue(new MongoId(qid), out var quest))
            {
                quest.Rewards ??= new();
                if (!quest.Rewards.TryGetValue("Success", out var success) || success == null)
                    quest.Rewards["Success"] = success = [];
                success.Add(Utils.TraderUnlockReward(traderId));
                unlocksWired++;
            }
            else
            {
                logger.Warning($"{Prefix} trader-unlock quest not found: {unlockQuestName} ({traderName})");
            }
        }

        // ---- Fence start requirement: gate Fence's first quest behind required quests ----
        var fenceFirstName = mainQuests.TryGetValue("FENCE", out var fenceList) && fenceList.Count > 0
            ? FirstQuestName(fenceList[0])
            : null;

        var fenceReqsAdded = 0;
        if (fenceFirstName != null && nameToId.TryGetValue(fenceFirstName, out var fenceFirstId) &&
            quests.TryGetValue(new MongoId(fenceFirstId), out var fenceFirstQuest) &&
            fenceFirstQuest.Conditions != null)
        {
            fenceFirstQuest.Conditions.AvailableForStart ??= [];
            foreach (var reqName in adjustments.FenceStartRequiredQuests)
            {
                if (Constants.RemoveList.Contains(reqName)) continue;
                if (!nameToId.TryGetValue(reqName, out var reqId)) continue;
                fenceFirstQuest.Conditions.AvailableForStart.Add(
                    Utils.AvailableForStartQuestRequirement(reqId, reqId + "fence"));
                fenceReqsAdded++;
            }
        }

        logger.Success(
            $"{Prefix} chain rebuild done. Traders chained: {chainsBuilt}. " +
            $"Trader unlocks wired: {unlocksWired}. Fence start reqs added: {fenceReqsAdded}.");
    }

    /// <summary>First quest name of a MainQuests entry (string, or first element of a group array).</summary>
    private static string? FirstQuestName(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String) return el.GetString();
        if (el.ValueKind == JsonValueKind.Array)
            foreach (var inner in el.EnumerateArray())
                if (inner.ValueKind == JsonValueKind.String) return inner.GetString();
        return null;
    }

    /// <summary>
    /// Phase 2c. Per trader: pull each quest's xp / money / trader-standing rewards into
    /// pools, sort ascending, and redistribute by chain position so reward scales with
    /// progression. Money is converted to the trader's native currency; negative rep is
    /// dropped. Ref gets bespoke scaling (few quests). Quests in containerRewardList are
    /// skipped entirely and instead granted their secure container (money reward removed).
    /// </summary>
    private void RebalanceRewards(Dictionary<MongoId, Quest> quests)
    {
        var nameToId = new Dictionary<string, string>();
        foreach (var (id, quest) in quests)
        {
            var name = quest.QuestName ?? "";
            if (name.Length > 0 && !nameToId.ContainsKey(name))
                nameToId[name] = id.ToString();
        }

        const string Exp = "experience";
        var rebalancedTraders = 0;
        var containersGranted = 0;

        foreach (var (trader, list) in mainQuests)
        {
            // Ordered flattened quest names for this trader (chain members inline).
            var names = new List<string>();
            foreach (var el in list)
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrEmpty(s)) names.Add(s);
                }
                else if (el.ValueKind == JsonValueKind.Array)
                {
                    foreach (var inner in el.EnumerateArray())
                        if (inner.ValueKind == JsonValueKind.String)
                        {
                            var s = inner.GetString();
                            if (!string.IsNullOrEmpty(s)) names.Add(s);
                        }
                }
            }

            // Exclude container quests from the rebalance entirely.
            var rebalanceNames = names.Where(n => !adjustments.ContainerRewardList.ContainsKey(n)).ToList();

            var expPool = new List<Reward>();
            var moneyPool = new List<Reward>();
            var standingPool = new List<Reward>();

            foreach (var name in rebalanceNames)
            {
                if (!nameToId.TryGetValue(name, out var qid) ||
                    !quests.TryGetValue(new MongoId(qid), out var quest) || quest.Conditions == null)
                    continue;

                var traderId = quest.TraderId.ToString();
                var traderCurrency = Constants.TraderCurrency.GetValueOrDefault(traderId, Constants.Roubles);

                Reward experience = Utils.ExperienceReward(name, 1200);
                Reward standing = Utils.StandingReward(name, traderId, 0.01);

                quest.Rewards ??= new();
                if (quest.Rewards.TryGetValue("Success", out var success) && success != null)
                {
                    var kept = new List<Reward>();
                    foreach (var rew in success)
                    {
                        var moneyTpl = rew.Items?.FirstOrDefault()?.Template.ToString();
                        if (moneyTpl != null && Constants.MoneyTpls.Contains(moneyTpl))
                        {
                            // Convert to the trader's currency, collect, drop from quest.
                            if (moneyTpl != traderCurrency)
                            {
                                var newValue = Utils.ConvertMoney(rew.Value ?? 0, moneyTpl, traderCurrency);
                                if (rew.Items!.First().Upd is { } upd) upd.StackObjectsCount = newValue;
                                rew.Items!.First().Template = new MongoId(traderCurrency);
                                rew.Value = newValue;
                            }
                            moneyPool.Add(rew);
                            continue;
                        }

                        if (rew.Type == SPTarkov.Server.Core.Models.Enums.RewardType.Experience)
                        {
                            experience = rew;
                            continue;
                        }

                        if (rew.Type == SPTarkov.Server.Core.Models.Enums.RewardType.TraderStanding &&
                            rew.Target?.ToString() == traderId)
                        {
                            standing = rew;
                            continue;
                        }

                        // Drop negative trader-standing penalties.
                        if (rew.Type == SPTarkov.Server.Core.Models.Enums.RewardType.TraderStanding &&
                            (rew.Value ?? 0) < 0)
                            continue;

                        kept.Add(rew);
                    }
                    quest.Rewards["Success"] = kept;
                }

                expPool.Add(experience);
                standingPool.Add(standing);
            }

            expPool.Sort((a, b) => (a.Value ?? 0).CompareTo(b.Value ?? 0));
            moneyPool.Sort((a, b) => (a.Value ?? 0).CompareTo(b.Value ?? 0));
            standingPool.Sort((a, b) => (a.Value ?? 0).CompareTo(b.Value ?? 0));

            // Ref special: few quests -> climb standing 0.1..n/10 and scale money.
            if (trader == "REF")
            {
                for (var i = 0; i < standingPool.Count; i++)
                    standingPool[i].Value = (i + 1) / 10.0;
                for (var i = 0; i < moneyPool.Count; i++)
                {
                    var add = i * adjustments.RefMoneyMultiplier;
                    moneyPool[i].Value = (moneyPool[i].Value ?? 0) + add;
                    if (moneyPool[i].Items?.FirstOrDefault()?.Upd is { } upd)
                        upd.StackObjectsCount = (upd.StackObjectsCount ?? 0) + add;
                }
            }

            // Reassign pooled rewards by chain position.
            for (var i = 0; i < rebalanceNames.Count; i++)
            {
                if (!nameToId.TryGetValue(rebalanceNames[i], out var qid) ||
                    !quests.TryGetValue(new MongoId(qid), out var quest))
                    continue;
                quest.Rewards ??= new();
                if (!quest.Rewards.TryGetValue("Success", out var success) || success == null)
                    quest.Rewards["Success"] = success = [];

                if (i < expPool.Count && (expPool[i].Value ?? 0) > 0) success.Add(expPool[i]);
                if (i < moneyPool.Count && (moneyPool[i].Value ?? 0) > 0) success.Add(moneyPool[i]);
                if (i < standingPool.Count && (standingPool[i].Value ?? 0) != 0) success.Add(standingPool[i]);
            }

            rebalancedTraders++;
        }

        // ---- container rewards (manual, after rebalance) ----
        foreach (var (questName, containerTpl) in adjustments.ContainerRewardList)
        {
            if (!nameToId.TryGetValue(questName, out var qid) ||
                !quests.TryGetValue(new MongoId(qid), out var quest))
                continue;

            quest.Rewards ??= new();
            if (!quest.Rewards.TryGetValue("Success", out var success) || success == null)
                quest.Rewards["Success"] = success = [];

            // Strip any money-item rewards (container is the prize).
            success.RemoveAll(r =>
            {
                var tpl = r.Items?.FirstOrDefault()?.Template.ToString();
                return tpl != null && Constants.MoneyTpls.Contains(tpl);
            });

            success.Add(Utils.ItemReward(questName, containerTpl));
            containersGranted++;
        }

        logger.Success(
            $"{Prefix} reward rebalance done. Traders rebalanced: {rebalancedTraders}. " +
            $"Containers granted: {containersGranted}.");
    }

    /// <summary>Flatten the curated MainQuests structure into all quest names (incl. chain members).</summary>
    private IEnumerable<string> FlattenMainQuests()
    {
        foreach (var list in mainQuests.Values)
        {
            foreach (var el in list)
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrEmpty(s)) yield return s;
                }
                else if (el.ValueKind == JsonValueKind.Array)
                {
                    foreach (var inner in el.EnumerateArray())
                        if (inner.ValueKind == JsonValueKind.String)
                        {
                            var s = inner.GetString();
                            if (!string.IsNullOrEmpty(s)) yield return s;
                        }
                }
            }
        }
    }

    /// <summary>
    /// Phase 2e. Redistribute each trader's quest-unlocked stock across its curated chain:
    /// pull every ASSORTMENT_UNLOCK reward off its (now-reordered) original quest, sort the
    /// unlocks by trader-loyalty level, and weight-assign them to curated quests so cheaper
    /// stock unlocks earlier (port of TS assort reassignment + assignQuestNamesWithWeight).
    /// </summary>
    private void ReassignAssorts(Dictionary<MongoId, Quest> quests)
    {
        var nameToId = new Dictionary<string, string>();
        foreach (var (id, quest) in quests)
        {
            var name = quest.QuestName ?? "";
            if (name.Length > 0 && !nameToId.ContainsKey(name))
                nameToId[name] = id.ToString();
        }

        var traders = databaseService.GetTables().Traders;
        if (traders == null)
        {
            logger.Warning($"{Prefix} no traders table; skipping assort reassignment.");
            return;
        }

        var totalReassigned = 0;
        var tradersTouched = 0;

        foreach (var (trader, list) in mainQuests)
        {
            if (!Constants.TraderIds.TryGetValue(trader, out var traderId)) continue;
            if (!traders.TryGetValue(new MongoId(traderId), out var traderData) || traderData == null) continue;

            var questAssort = traderData.QuestAssort;
            var assort = traderData.Assort;
            if (questAssort == null || !questAssort.TryGetValue("success", out var successMap) ||
                successMap == null || successMap.Count == 0 || assort?.LoyalLevelItems == null)
                continue;

            // Ordered curated quest names for this trader.
            var names = new List<string>();
            foreach (var el in list)
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrEmpty(s)) names.Add(s);
                }
                else if (el.ValueKind == JsonValueKind.Array)
                    foreach (var inner in el.EnumerateArray())
                        if (inner.ValueKind == JsonValueKind.String)
                        {
                            var s = inner.GetString();
                            if (!string.IsNullOrEmpty(s)) names.Add(s);
                        }
            }
            if (names.Count == 0) continue;

            // Collect (assortItemKey, level, rewardObject), pulling the reward off the old quest.
            var entries = new List<(MongoId key, int level, Reward reward)>();
            foreach (var (assortKey, oldQuestId) in successMap)
            {
                if (!quests.TryGetValue(oldQuestId, out var oldQuest) || oldQuest.Rewards == null) continue;
                if (!oldQuest.Rewards.TryGetValue("Success", out var oldSuccess) || oldSuccess == null) continue;

                // Prefer the unlock whose target is this assort item; else the first unclaimed unlock.
                var reward = oldSuccess.FirstOrDefault(r =>
                                 r.Type == SPTarkov.Server.Core.Models.Enums.RewardType.AssortmentUnlock &&
                                 r.Target?.ToString() == assortKey.ToString())
                             ?? oldSuccess.FirstOrDefault(r =>
                                 r.Type == SPTarkov.Server.Core.Models.Enums.RewardType.AssortmentUnlock);
                if (reward == null) continue;

                oldSuccess.Remove(reward);
                var level = assort.LoyalLevelItems.GetValueOrDefault(assortKey, 1);
                entries.Add((assortKey, level, reward));
            }
            if (entries.Count == 0) continue;

            entries.Sort((a, b) => a.level.CompareTo(b.level));

            var assigned = Utils.AssignQuestNamesWithWeight(names, entries.Count, adjustments.UnlockAssortWeightFactorZeroToOne);

            for (var i = 0; i < entries.Count; i++)
            {
                var name = assigned[i];
                if (string.IsNullOrEmpty(name) || !nameToId.TryGetValue(name, out var qid) ||
                    !quests.TryGetValue(new MongoId(qid), out var newQuest))
                    continue;

                successMap[entries[i].key] = new MongoId(qid);

                newQuest.Rewards ??= new();
                if (!newQuest.Rewards.TryGetValue("Success", out var success) || success == null)
                    newQuest.Rewards["Success"] = success = [];
                success.Add(entries[i].reward);
                totalReassigned++;
            }

            tradersTouched++;
        }

        logger.Success(
            $"{Prefix} assort reassignment done. Traders: {tradersTouched}. Unlocks reassigned: {totalReassigned}.");
    }

    /// <summary>
    /// Phase 2d. Trader / quest-config tweaks:
    ///   - clear BEAR/USEC faction-only quest gates (everyone can do every quest),
    ///   - optionally disable repeatable "daily" quests (config.DisableDailies),
    ///   - set ammo trader-loyalty levels from ammoLevelUnlocks.json (pen-ordered),
    ///     so better ammo unlocks at higher loyalty in a logical progression.
    /// </summary>
    private void ApplyTraderTweaks()
    {
        var questConfig = configServer.GetConfig<QuestConfig>();

        // --- faction gates: everyone can do every quest ---
        if (questConfig != null)
        {
            questConfig.BearOnlyQuests = [];
            questConfig.UsecOnlyQuests = [];
        }

        // --- dailies (config-gated) ---
        var dailiesDisabled = false;
        if (config.DisableDailies && questConfig?.RepeatableQuests != null)
        {
            foreach (var rq in questConfig.RepeatableQuests)
            {
                rq.NumQuests = 0;
                rq.TraderWhitelist = [];
            }
            dailiesDisabled = true;
        }

        // --- ammo loyalty levels (pen-ordered, from ammoLevelUnlocks.json) ---
        // Flatten to tpl -> level (level is a property of the ammo's penetration, so it's
        // consistent for a given tpl regardless of which trader/caliber bucket it sits in).
        var tplToLevel = new Dictionary<string, int>();
        foreach (var bucket in ammoUnlocks.Values)
            foreach (var a in bucket)
                if (!string.IsNullOrEmpty(a.Tpl))
                    tplToLevel[a.Tpl] = a.Level;

        var ammoLevelsSet = 0;
        var traders = databaseService.GetTables().Traders;
        if (traders != null)
        {
            foreach (var trader in traders.Values)
            {
                var assort = trader?.Assort;
                if (assort?.Items == null || assort.LoyalLevelItems == null) continue;

                foreach (var item in assort.Items)
                {
                    var tpl = item.Template.ToString();
                    if (!tplToLevel.TryGetValue(tpl, out var level)) continue;
                    if (assort.LoyalLevelItems.TryGetValue(item.Id, out var current) && current == level) continue;
                    assort.LoyalLevelItems[item.Id] = level;
                    ammoLevelsSet++;
                }
            }
        }

        logger.Success(
            $"{Prefix} trader tweaks done. Faction gates cleared. " +
            $"Dailies disabled: {dailiesDisabled}. Ammo loyalty levels set: {ammoLevelsSet}.");
    }

    /// <summary>Seasonal event quest ids (a real season, not "None") from the quest config.</summary>
    private HashSet<string> GetSeasonalEventQuestIds()
    {
        var result = new HashSet<string>();
        var questConfig = configServer.GetConfig<QuestConfig>();
        var events = questConfig?.EventQuests;
        if (events == null) return result;

        foreach (var (id, ev) in events)
        {
            var season = ev?.Season.ToString();
            if (!string.IsNullOrEmpty(season) && !season.Equals("None", StringComparison.OrdinalIgnoreCase))
                result.Add(id);
        }
        return result;
    }

    /// <summary>
    /// True if a quest can ONLY be completed during an event — i.e. it has a kill
    /// objective whose SavageRole list is non-empty and contains ONLY event-only roles.
    /// Mixed objectives (e.g. bossTagilla/followerTagilla/infectedTagilla) are completable
    /// via the normal enemy and are NOT removed.
    /// </summary>
    private static bool RequiresEventEnemy(Quest quest)
    {
        var finish = quest.Conditions?.AvailableForFinish;
        if (finish == null) return false;
        foreach (var c in finish)
        {
            var inner = c.Counter?.Conditions;
            if (inner == null) continue;
            foreach (var ic in inner)
            {
                var roles = ic.SavageRole;
                if (roles == null || roles.Count == 0) continue;
                // Only blocking if EVERY allowed role is event-only.
                if (roles.All(r => r != null && EventOnlyRoles.Contains(r)))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// A real "transit between maps" objective = a counter condition whose Status is
    /// exactly ["Transit"] (NOT a normal extract whose status is ["Survived","Transit"]).
    /// </summary>
    private static bool IsRealTransitCondition(QuestCondition condition)
    {
        var inner = condition.Counter?.Conditions;
        if (inner == null) return false;
        foreach (var ic in inner)
        {
            var status = ic.Status;
            if (status != null && status.Count == 1 &&
                string.Equals(status[0].ToString(), "Transit", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
