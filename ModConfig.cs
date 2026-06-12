using System.Text.Json.Serialization;

namespace AlgorithmicQuestingProgression;

/// <summary>
/// Typed representation of <c>config/config.json</c>.
/// Field names mirror the original TypeScript mod's config, with two new
/// standalone toggles ported from Lacyway's PvE Tweaks (transit removal + Ref quests).
/// </summary>
public record ModConfig
{
    // --- Module toggles ---

    [JsonPropertyName("enableOverhaulModule")]
    public bool EnableOverhaulModule { get; set; } = true;

    [JsonPropertyName("enableAdjusterModule")]
    public bool EnableAdjusterModule { get; set; } = true;

    /// <summary>NEW — remove map-transit requirements (port of Lacy's EditTransits).</summary>
    [JsonPropertyName("removeTransitQuests")]
    public bool RemoveTransitQuests { get; set; } = true;

    /// <summary>NEW — Ref/Arena questline is delegated to Lacy's PvE Tweaks (its own
    /// refChanges option); AQP leaves all Arena-trader quests untouched. Kept as an
    /// informational toggle only.</summary>
    [JsonPropertyName("refChanges")]
    public bool RefChanges { get; set; } = false;

    // --- Adjuster modifiers ---

    [JsonPropertyName("disableDailies")]
    public bool DisableDailies { get; set; } = true;

    /// <summary>
    /// NEW — when true, on server start strip every launcher profile template EXCEPT
    /// "SPT Zero to hero", so it's the only selectable profile option.
    /// </summary>
    [JsonPropertyName("onlyZeroToHeroProfile")]
    public bool OnlyZeroToHeroProfile { get; set; } = false;

    [JsonPropertyName("baseKillCountQuantity")]
    public double BaseKillCountQuantity { get; set; } = 4;

    [JsonPropertyName("killCountModifier")]
    public double KillCountModifier { get; set; } = 0.5;

    [JsonPropertyName("questLevelUnlockModifier")]
    public double QuestLevelUnlockModifier { get; set; } = 1;

    [JsonPropertyName("questExperienceModifier")]
    public double QuestExperienceModifier { get; set; } = 1;

    [JsonPropertyName("traderStandingRewardModifier")]
    public double TraderStandingRewardModifier { get; set; } = 1;

    [JsonPropertyName("itemRewardModifier")]
    public double ItemRewardModifier { get; set; } = 1;

    [JsonPropertyName("killQuestCountModifier")]
    public double KillQuestCountModifier { get; set; } = 1;

    [JsonPropertyName("findItemQuestModifier")]
    public double FindItemQuestModifier { get; set; } = 0.5;

    [JsonPropertyName("plantTimeModifier")]
    public double PlantTimeModifier { get; set; } = 0.2;

    [JsonPropertyName("replaceGunsmith")]
    public bool ReplaceGunsmith { get; set; } = true;

    // --- Debug ---

    [JsonPropertyName("overHaulDebug")]
    public bool OverHaulDebug { get; set; }

    [JsonPropertyName("adjusterDebug")]
    public bool AdjusterDebug { get; set; }
}
