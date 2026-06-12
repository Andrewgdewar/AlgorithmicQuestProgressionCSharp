using System.Text.Json.Serialization;

namespace AlgorithmicQuestingProgression;

/// <summary>
/// Typed representation of <c>config/questAdjustments.json</c> — the per-quest
/// curation data authored for the overhaul. Mirrors the old TS mod's questAdjustments
/// plus the new C# additions (weaponBuildStripList, skillConditionStripList,
/// containerRewardList).
/// </summary>
public record QuestAdjustments
{
    /// <summary>How many prior quests a quest requires before it unlocks, per trader.</summary>
    [JsonPropertyName("TraderQuestProgressionQuantity")]
    public Dictionary<string, int> TraderQuestProgressionQuantity { get; set; } = new();

    /// <summary>Quest that unlocks each trader ("" = unlocked by default).</summary>
    [JsonPropertyName("TraderUnlockQuests")]
    public Dictionary<string, string> TraderUnlockQuests { get; set; } = new();

    /// <summary>Quests that must be completed before Fence's first quest is available.</summary>
    [JsonPropertyName("FenceStartRequiredQuests")]
    public List<string> FenceStartRequiredQuests { get; set; } = new();

    /// <summary>
    /// questName -> { "id": [conditionIds] }. Each listed AvailableForFinish condition
    /// (matched by its id) is deleted from the quest.
    /// </summary>
    [JsonPropertyName("deleteReqList")]
    public Dictionary<string, Dictionary<string, List<string>>> DeleteReqList { get; set; } = new();

    /// <summary>
    /// questName -> { conditionId -> { field merge } }. Shallow-merges the given fields
    /// (value / oneSessionOnly) onto the matching AvailableForFinish condition.
    /// </summary>
    [JsonPropertyName("adjustReqsList")]
    public Dictionary<string, Dictionary<string, AdjustFields>> AdjustReqsList { get; set; } = new();

    /// <summary>
    /// Quests whose Kills conditions should keep only the base weapon — clears
    /// weaponModsInclusive + weaponModsExclusive on every inner Kills counter condition.
    /// </summary>
    [JsonPropertyName("weaponBuildStripList")]
    public List<string> WeaponBuildStripList { get; set; } = new();

    /// <summary>
    /// Quests whose Skill-type AvailableForFinish condition should be removed (keeping
    /// all other objectives). Skill-only quests become a pass-through in their chain.
    /// </summary>
    [JsonPropertyName("skillConditionStripList")]
    public List<string> SkillConditionStripList { get; set; } = new();

    /// <summary>
    /// questName -> container template id. These quests award a secure container as
    /// their prize; the reward-rebalance pass must IGNORE them (placed manually).
    /// </summary>
    [JsonPropertyName("containerRewardList")]
    public Dictionary<string, string> ContainerRewardList { get; set; } = new();

    [JsonPropertyName("unlockAssortWeightFactorZeroToOne")]
    public double UnlockAssortWeightFactorZeroToOne { get; set; } = 0.7;

    [JsonPropertyName("refMoneyMultiplier")]
    public double RefMoneyMultiplier { get; set; } = 20;
}

/// <summary>Partial field set merged onto an AvailableForFinish condition by adjustReqsList.</summary>
public record AdjustFields
{
    [JsonPropertyName("value")]
    public double? Value { get; set; }

    [JsonPropertyName("oneSessionOnly")]
    public bool? OneSessionOnly { get; set; }

    /// <summary>
    /// Optional: replace the condition's target with this list of template ids, so the
    /// objective accepts ANY of them (e.g. hand over N of any figurine from a set).
    /// </summary>
    [JsonPropertyName("target")]
    public List<string>? Target { get; set; }
}
