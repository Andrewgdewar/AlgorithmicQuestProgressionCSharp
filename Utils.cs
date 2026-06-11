using System.Security.Cryptography;
using System.Text;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace AlgorithmicQuestingProgression;

/// <summary>
/// Small helpers ported from the old TS mod's Utils/transformMethods.
/// </summary>
public static class Utils
{
    /// <summary>
    /// Deterministic MongoId from a seed string — MD5(seed) hex, first 24 chars,
    /// right-padded with zeros to 24. Byte-identical to the old TS
    /// <c>generateMongoIdFromSeed</c> so quest links/profiles stay stable across runs.
    /// </summary>
    public static string GenerateMongoIdFromSeed(string seed)
    {
        if (string.IsNullOrEmpty(seed))
            throw new ArgumentException("Seed is required to generate a MongoID.", nameof(seed));

        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(seed));
        var hex = Convert.ToHexStringLower(bytes); // 32 hex chars
        return hex.Length >= 24 ? hex[..24] : hex.PadRight(24, '0');
    }

    /// <summary>
    /// Build a "player must be at least this level" start condition
    /// (port of TS <c>AvailableForStartLevelRequirement</c>).
    /// </summary>
    public static QuestCondition AvailableForStartLevelRequirement(int level, string seed) => new()
    {
        CompareMethod = ">=",
        ConditionType = "Level",
        DynamicLocale = false,
        GlobalQuestCounterId = "",
        Id = new MongoId(GenerateMongoIdFromSeed(seed)),
        Index = 1,
        ParentId = "",
        Value = level,
        VisibilityConditions = [],
    };

    /// <summary>
    /// Build a "must have completed quest X" start condition
    /// (port of TS <c>AvailableForStartQuestRequirement</c>). status [4] = Success.
    /// </summary>
    public static QuestCondition AvailableForStartQuestRequirement(string questId, string seed) => new()
    {
        AvailableAfter = 0,
        ConditionType = "Quest",
        Dispersion = 0,
        DynamicLocale = false,
        GlobalQuestCounterId = "",
        Id = new MongoId(GenerateMongoIdFromSeed(questId + seed)),
        Index = 0,
        ParentId = "",
        Status = [SPTarkov.Server.Core.Models.Enums.QuestStatusEnum.Success],
        Target = new(null, questId),
        VisibilityConditions = [],
    };

    /// <summary>
    /// Walk an ordered quest-id list, grouping it into sets of <paramref name="quantity"/>,
    /// and add "must complete the previous set" start requirements to each quest in every
    /// set after the first (port of TS <c>IterateOverArrayAddingQuestReqs</c>). Empty ids
    /// are kept as placeholders to preserve set alignment but are skipped when applied.
    /// </summary>
    public static void IterateOverArrayAddingQuestReqs(
        Dictionary<MongoId, Quest> quests, List<string> questIdList, int quantity = 1)
    {
        if (quantity < 1) quantity = 1;

        var sets = new List<List<string>>();
        for (var i = 0; i < questIdList.Count; i += quantity)
            sets.Add(questIdList.GetRange(i, Math.Min(quantity, questIdList.Count - i)));

        for (var i = 1; i < sets.Count; i++)
        {
            var prev = sets[i - 1];
            foreach (var qid in sets[i])
            {
                if (string.IsNullOrEmpty(qid) || !quests.TryGetValue(new MongoId(qid), out var quest))
                    continue;
                if (quest.Conditions == null) continue;
                quest.Conditions.AvailableForStart ??= [];
                foreach (var prevId in prev)
                {
                    if (string.IsNullOrEmpty(prevId)) continue;
                    quest.Conditions.AvailableForStart.Add(
                        AvailableForStartQuestRequirement(prevId, prevId + "prevQuest"));
                }
            }
        }
    }

    /// <summary>
    /// Build a TRADER_UNLOCK success reward (port of TS <c>traderUnlockSuccessByID</c>).
    /// Completing the owning quest unlocks the target trader.
    /// </summary>
    public static Reward TraderUnlockReward(string traderId) => new()
    {
        AvailableInGameEditions = [],
        Id = new MongoId(GenerateMongoIdFromSeed(traderId)),
        Index = 0,
        Target = traderId,
        Type = SPTarkov.Server.Core.Models.Enums.RewardType.TraderUnlock,
    };
}
