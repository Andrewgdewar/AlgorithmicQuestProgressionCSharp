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
    /// Build a "fail this quest when quest X succeeds" Fail condition. Used to fork two
    /// mutually-exclusive quests (Gunsmith / Deathsmith): completing one auto-fails the
    /// other. status [4] = Success.
    /// </summary>
    public static QuestCondition FailOnQuestSuccess(string questId, string seed) => new()
    {
        AvailableAfter = 0,
        ConditionType = "Quest",
        Dispersion = 0,
        DynamicLocale = false,
        GlobalQuestCounterId = "",
        Id = new MongoId(GenerateMongoIdFromSeed(seed)),
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

    /// <summary>
    /// Convert an amount between Tarkov currencies via static rouble exchange rates
    /// (port of TS <c>convertMoney</c>). Rounds to nearest whole.
    /// </summary>
    public static double ConvertMoney(double amount, string fromTpl, string toTpl)
    {
        if (amount < 0) amount = 0;
        if (fromTpl == toTpl) return amount;
        var from = Constants.ExchangeRates.GetValueOrDefault(fromTpl, 1);
        var to = Constants.ExchangeRates.GetValueOrDefault(toTpl, 1);
        return Math.Round(amount * from / to);
    }

    /// <summary>
    /// Distribute <paramref name="count"/> assort unlocks across an ordered quest-name list,
    /// weighted toward the front by <paramref name="weightFactor"/> (0..1) so lower-level
    /// stock unlocks at earlier quests (port of TS <c>assignQuestNamesWithWeight</c>).
    /// Returns a list parallel to the (level-sorted) assort list: index i -> quest name.
    /// </summary>
    public static List<string?> AssignQuestNamesWithWeight(List<string> questNames, int count, double weightFactor)
    {
        if (weightFactor < 0 || weightFactor > 1)
            throw new ArgumentException("Weight factor must be between 0 and 1.", nameof(weightFactor));

        var result = new List<string?>(count);
        var totalQuests = questNames.Count;
        for (var i = 0; i < count; i++)
        {
            var normalized = count <= 1 ? 0 : (double)i / (count - 1) * weightFactor;
            var skewed = (int)Math.Round(normalized * totalQuests);
            if (skewed >= totalQuests) skewed = totalQuests - 1;
            if (skewed < 0) skewed = 0;
            result.Add(totalQuests > 0 ? questNames[skewed] : null);
        }
        return result;
    }

    /// <summary>Build an EXPERIENCE success reward.</summary>
    public static Reward ExperienceReward(string seed, double value) => new()
    {
        AvailableInGameEditions = [],
        Id = new MongoId(GenerateMongoIdFromSeed(seed + "experience")),
        Index = 0,
        Type = SPTarkov.Server.Core.Models.Enums.RewardType.Experience,
        Value = value,
    };

    /// <summary>Build a TRADER_STANDING success reward for the given trader.</summary>
    public static Reward StandingReward(string seed, string traderId, double value) => new()
    {
        AvailableInGameEditions = [],
        Id = new MongoId(GenerateMongoIdFromSeed(seed + "standing")),
        Index = 0,
        Target = traderId,
        Type = SPTarkov.Server.Core.Models.Enums.RewardType.TraderStanding,
        Value = value,
    };

    /// <summary>Build an ITEM success reward granting a single item template (e.g. a container).</summary>
    public static Reward ItemReward(string seed, string itemTpl)
    {
        var rewardItemId = new MongoId(GenerateMongoIdFromSeed(seed + "containerItem"));
        return new Reward
        {
            AvailableInGameEditions = [],
            FindInRaid = true,
            Id = new MongoId(GenerateMongoIdFromSeed(seed + "container")),
            Index = 0,
            Type = SPTarkov.Server.Core.Models.Enums.RewardType.Item,
            Value = 1,
            Target = rewardItemId.ToString(),
            Items =
            [
                new Item
                {
                    Id = rewardItemId,
                    Template = new MongoId(itemTpl),
                    Upd = new Upd { StackObjectsCount = 1, SpawnedInSession = true },
                },
            ],
        };
    }

    /// <summary>
    /// Parse the trailing number out of a Gunsmith quest name (port of TS
    /// <c>getNumbersFromName</c>). "Gunsmith - Old Friends Request" is special-cased to 27;
    /// names with no digits return 0.
    /// </summary>
    public static int GetNumbersFromName(string name)
    {
        if (name == "Gunsmith - Old Friends Request") return 27;
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, out var n) ? n : 0;
    }

    /// <summary>
    /// Build the replacement "eliminate N enemies with &lt;weapon&gt;" CounterCreator condition
    /// for a Gunsmith quest (port of TS <c>getKillQuestForGunsmith</c>/<c>defaultKillQuest</c>).
    /// Kill count = round(baseKillCountQuantity + questNumber * killCountModifier).
    /// </summary>
    public static QuestCondition GunsmithKillCondition(int questNumber, string weaponTpl, double baseKillCount, double killModifier, string seedSuffix = "")
    {
        var totalBots = (int)Math.Round(baseKillCount + questNumber * killModifier);
        if (totalBots < 1) totalBots = 1;

        var innerId = new MongoId(GenerateMongoIdFromSeed("conditionId" + questNumber + seedSuffix));
        var counterId = new MongoId(GenerateMongoIdFromSeed("counterId" + questNumber + seedSuffix));
        var conditionId = new MongoId(GenerateMongoIdFromSeed("questId" + questNumber + seedSuffix));

        return new QuestCondition
        {
            ConditionType = "CounterCreator",
            DynamicLocale = false,
            GlobalQuestCounterId = "",
            Id = conditionId,
            Index = 0,
            IsNecessary = false,
            ParentId = "",
            OneSessionOnly = false,
            Value = totalBots,
            VisibilityConditions = [],
            Counter = new QuestConditionCounter
            {
                Id = counterId,
                Conditions =
                [
                    new QuestConditionCounterCondition
                    {
                        BodyPart = [],
                        CompareMethod = ">=",
                        ConditionType = "Kills",
                        DynamicLocale = false,
                        Id = innerId,
                        ResetOnSessionEnd = false,
                        SavageRole = [],
                        Target = new(null, "Any"),
                        Value = 1,
                        Weapon = [weaponTpl],
                        WeaponModsInclusive = new List<List<string>>(),
                        WeaponModsExclusive = new List<List<string>>(),
                    },
                ],
            },
        };
    }
}
