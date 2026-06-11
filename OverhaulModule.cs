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
    QuestAdjustments adjustments)
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
