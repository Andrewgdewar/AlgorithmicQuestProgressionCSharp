using System.Text.Json.Serialization;

namespace AlgorithmicQuestingProgression;

/// <summary>
/// One ammo entry in <c>config/ammoLevelUnlocks.json</c>. The file is a static,
/// pre-computed mapping (carried over from the previous version) that assigns each
/// ammo tpl a trader loyalty <see cref="Level"/> (1-4) by penetration — so better
/// ammo unlocks at higher loyalty. Only <see cref="Tpl"/> and <see cref="Level"/>
/// are used at runtime; the rest is reference metadata.
/// </summary>
public record AmmoUnlock
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("tpl")]
    public string Tpl { get; set; } = "";

    [JsonPropertyName("level")]
    public int Level { get; set; } = 1;

    [JsonPropertyName("damage")]
    public int Damage { get; set; }

    [JsonPropertyName("pen")]
    public int Pen { get; set; }

    [JsonPropertyName("trader")]
    public string Trader { get; set; } = "";
}
