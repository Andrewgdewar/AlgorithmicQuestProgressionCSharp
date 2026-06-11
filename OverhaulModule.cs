using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;

namespace AlgorithmicQuestingProgression;

/// <summary>
/// OverhaulModule — phase 1 "teardown" pass (port of the old OverHaulModule's
/// remove + disassociate steps). Rebuilding the linear chain happens in a later phase.
///
/// Order of operations:
///   1. REMOVE quests: removeList + seasonal-event quests + event-enemy quests
///      (zombies / Halloween cultists / Bloodhound — can't be done in PvE).
///      Arena PvP quests are NOT removed here (RefModule rewrites them).
///   2. TRANSIT: for every remaining quest, drop "transit between maps" finish
///      conditions; if a quest's ONLY finish conditions were transit, remove it.
///   3. LEVELS: strip every Level start-requirement from every quest.
///   4. DISASSOCIATE: clear cross-quest links so the curated chain can be rebuilt —
///      remove Quest-type conditions from AvailableForStart / AvailableForFinish / Fail,
///      clear rewards["Fail"], set restartable = true, fix dangling VisibilityConditions.
/// </summary>
public class OverhaulModule(
    ISptLogger<AlgorithmicQuestingProgression> logger,
    DatabaseService databaseService,
    ConfigServer configServer,
    ModConfig config)
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

            // --- disassociate: strip cross-quest links so the chain can be rebuilt cleanly ---
            // Arena PvP quests are left intact for the RefModule to rewrite.
            if (quest.TraderId != ArenaTraderId)
            {
                crossLinksStripped += conds.AvailableForStart?.RemoveAll(c => c.ConditionType == "Quest") ?? 0;
                crossLinksStripped += finish?.RemoveAll(c => c.ConditionType == "Quest") ?? 0;
                crossLinksStripped += conds.Fail?.RemoveAll(c => c.ConditionType == "Quest") ?? 0;

                // clear fail rewards + make restartable (no fail-lockouts between branching quests)
                if (quest.Rewards != null && quest.Rewards.TryGetValue("Fail", out var failRewards))
                    failRewards?.Clear();
                quest.Restartable = true;
            }
        }

        logger.Success(
            $"{Prefix} teardown done. Removed: {removedRemoveList} (removeList), {removedEvent} (seasonal), " +
            $"{removedEventEnemy} (event-enemy), {removedPureTransit} (pure-transit). " +
            $"Stripped: {levelsStripped} level reqs, {transitStripped} transit conditions, {crossLinksStripped} cross-quest links. " +
            $"Quests remaining: {quests.Count}");
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

    /// <summary>True if any kill objective requires an event-only enemy role.</summary>
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
                if (roles == null) continue;
                foreach (var r in roles)
                    if (r != null && EventOnlyRoles.Contains(r))
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
