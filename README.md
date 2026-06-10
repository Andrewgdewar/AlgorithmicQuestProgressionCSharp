# Algorithmic Questing Progression — C# (SPT 4.0)

A from-scratch C# port + modernization of the TypeScript mod
[AlgorithmicQuestingProgression](https://github.com/Andrewgdewar/AlgorithmicQuestingProgression),
targeting the SPT 4.0 .NET server (same platform as `botplacementsystem-csharp`).

> Status: **PLANNING** — no code yet. This document is the build plan.

---

## 1. What the original mod does

The original is a `postDBLoad` TypeScript mod with **two independent modules**, each
toggled in `config.json`:

### A. OverHaul Module (`OverHaulModule.ts`) — the big one
Rebuilds the entire quest tree into a **linear, trader-by-trader progression** using a
manually-curated ordered list (`config/QuestConfigs/MainQuests.json`). This is the list
the author "manually went through." For each trader it:

1. **Removes junk quests** (`Constants.ts removeList` — deprecated/event/problematic quests are deleted from `quests`).
2. **Flattens** the per-trader quest order (supports sub-chains: an entry can be a string or an array-of-strings for parallel branches).
3. **Maps QuestName → questId** (reverse lookup over `quests`, last-wins dedupe).
4. **Quests NOT in the list** → `AvailableForStart` set to a level-99 requirement (effectively hidden).
5. **Quests IN the list** → `AvailableForStart` cleared, then rebuilt:
   - Strips cross-quest `AvailableForFinish` + `Fail` conditions (so quests don't depend on un-included quests).
   - `rewards.Fail = []`, `restartable = true`.
   - Re-links each quest to require the previous N quests via `AvailableForStartQuestRequirement` (`TraderQuestProgressionQuantity[trader]` controls N — how many prior quests gate the next).
   - Sub-chains re-linked with N=1.
6. **Trader unlock chains** (`TraderUnlockQuests`): sets `trader.base.unlockedByDefault = false` and pushes a `TRADER_UNLOCK` reward onto the gating quest. Traders not gated → `unlockedByDefault = true`.
7. **Fence / Kappa unlock**: appends `FenceStartRequiredQuests` as start-requirements on Fence's first quest.
8. **Reward rebalancing**: per-trader, recomputes EXPERIENCE / money / TRADER_STANDING rewards along the chain (scaling by index; `refMoneyMultiplier`), normalizes money to the trader's currency (`TraderCurrencies`).
9. **Assort loyalty reassignment**: walks each trader's `questassort.success` + `assort.loyal_level_items`, reassigns assort unlock levels along the new progression (`manualAssortReassignment` for manual overrides), incl. **ammo level unlocks** (`ammoLevelUnlocks.json` keyed by calibre).
10. **Per-quest condition fixups**: `deleteReqList` (remove specific finish conditions) and `adjustReqsList` (patch specific finish conditions).
11. Optionally **disables dailies** (`disableDailies` → zero out `repeatableQuests`).

### B. Adjuster Module (`AdjusterModule.ts`) — scalar tuning
Independent multipliers applied to every quest (does NOT restructure):
- `questLevelUnlockModifier` — scale `AvailableForStart` Level condition values.
- `killQuestCountModifier` — scale `CounterCreator` kill counts.
- `findItemQuestModifier` — scale `FindItem`/`HandoverItem` counts (skips real quest items).
- `plantTimeModifier` — scale `LeaveItemAtLocation`/`PlaceBeacon` plant times.
- `questExperienceModifier`, `itemRewardModifier`, `traderStandingRewardModifier` — scale success rewards.
- `replaceGunsmith` — replace WeaponAssembly quests with a kill quest (rewrites locale text).

### Config surface (TS)
- `config/config.json` — module toggles + debug flags + all scalar modifiers + `disableDailies`.
- `config/QuestConfigs/MainQuests.json` — the curated per-trader ordered quest-name lists (the heart of the mod).
- `config/QuestConfigs/questAdjustments.json` — `deleteReqList`, `adjustReqsList`, `TraderUnlockQuests`, `TraderQuestProgressionQuantity`, `FenceStartRequiredQuests`, `refMoneyMultiplier`, `manualAssortReassignment`, `unlockAssortWeightFactorZeroToOne`.
- `config/QuestConfigs/ammoLevelUnlocks.json` — calibre → ammo unlock tiers.
- `Constants.ts removeList` — quests to delete.

---

## 2. Target platform (SPT 4.0 C#)

Confirmed against `botplacementsystem-csharp` (working reference) and `server-mod-examples`:

- **Entry**: a `record ModMetadata : AbstractModMetadata` + an `[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + N)]` class implementing `IOnLoad`.
- **DB access**: inject `DatabaseService` (e.g. `databaseService.GetTables().Templates.Quests`, `.Traders`, `.Locales`). See `server-mod-examples/2EditDatabase/EditDatabaseValues.cs`.
- **SPT config access**: inject `ConfigServer` and `GetConfig<IQuestConfig>(ConfigTypes.QUEST)` for `repeatableQuests` / `bearOnlyQuests` / `usecOnlyQuests`. See `server-mod-examples/3EditSptConfig`.
- **Custom JSON config**: read our own JSON from the mod folder. See `server-mod-examples/5ReadCustomJsonConfig`.
- **Logging**: inject `ISptLogger<T>`.
- **Quest data is essentially identical** to the TS version: confirmed local `SPT_Data/database/templates/quests.json` has **558 quests**, each with `QuestName`, `_id`, `traderId`, `conditions.{AvailableForStart, AvailableForFinish, Fail}`, `rewards`, `type` — same shape the TS mod manipulates. The `.d.ts` types in the old repo map directly to SPT 4.0 C# model classes (`IQuest`, `IQuestCondition`, `IReward`, `ITrader`, etc.).

### Key API mapping (TS → C#)

| TS (old) | C# (SPT 4.0) |
|---|---|
| `container.resolve<DatabaseServer>().getTables()` | injected `DatabaseService.GetTables()` |
| `tables.templates.quests` (Record) | `Templates.Quests` (Dictionary<string, IQuest>) |
| `tables.traders` | `Tables.Traders` (Dictionary<string, ITrader>) |
| `tables.locales.global` / `.languages` | `Tables.Locales.Global` / `.Languages` |
| `configServer.getConfig<IQuestConfig>(ConfigTypes.QUEST)` | `ConfigServer.GetConfig<QuestConfig>(...)` |
| `RewardType.TRADER_UNLOCK` etc. | `RewardType` enum |
| `Traders.PRAPOR` enum | `Traders` enum |
| `generateMongoIdFromSeed` | port deterministic Mongo-id-from-seed helper |
| `cloneDeep` | `ICloner` service or manual clone |

---

## 3. Proposed architecture (C#)

Mirror the ABPS layout (`Server/` project, `Controllers/`, `Globals/`, `Models/`, data JSON copied via csproj `CopyToOutputDirectory`).

```
AlgorithmicQuestingProgression-csharp/
  Server/
    AlgorithmicQuestingProgression.csproj
    QuestProgression.cs            # ModMetadata + IOnLoad entry (calls modules)
    Controllers/
      OverhaulController.cs        # port of OverHaulModule
      AdjusterController.cs        # port of AdjusterModule
    Globals/
      ModConfig.cs                 # loads config.json + QuestConfigs/*.json into typed models
    Models/
      AqpConfig.cs                 # typed config (toggles, modifiers)
      QuestAdjustments.cs          # deleteReqList/adjustReqsList/TraderUnlockQuests/etc.
      MainQuests.cs                # per-trader ordered lists model
    Utils/
      QuestTransforms.cs           # AvailableForStart*/traderUnlock builders, MongoId-from-seed
      Constants.cs                 # removeList
    config.json
    QuestConfigs/
      MainQuests.json              # generated list (carry over from TS repo, diff vs 558-quest DB — see §4a)
      questAdjustments.json
      ammoLevelUnlocks.json
  .gitignore
  README.md  (this file)
  .git/      (own repository)
```

---

## 4. Effort estimate & risk

**Overall: moderate.** The logic is well-understood and data shapes match. Biggest cost
is faithful porting of the OverHaul reward/assort rebalancing and re-validating the
curated `MainQuests.json` against the current 558-quest DB (quest names drift between
wipes).

| Area | Difficulty | Notes |
|---|---|---|
| Project scaffold + ModMetadata + IOnLoad | Easy | Copy ABPS pattern |
| Config loading (typed JSON) | Easy | `server-mod-examples/5ReadCustomJsonConfig` |
| AdjusterModule port | Easy–Med | Pure scalar loops; locale rewrite for gunsmith is the only fiddly bit |
| OverhaulModule: remove/flatten/link | Med | Direct port; careful with sub-chain branching |
| OverhaulModule: trader unlock + fence/kappa | Med | Trader base flag + reward push |
| OverhaulModule: reward rebalance | Med–Hard | Index-scaled exp/money/standing, currency normalization |
| OverhaulModule: assort loyalty + ammo tiers | Hard | Most intricate; touches `questassort` + `loyal_level_items` |
| Re-validate MainQuests.json vs current DB | Med | Quest names/IDs change across wipes — must diff |
| `generateMongoIdFromSeed` parity | Easy | Port the deterministic hash so re-runs are stable |

**Risks**
- Curated quest list staleness: names that no longer exist must be reported (the TS mod just `console.log`s `!!! not found !!!` — we'll keep that and surface a summary).
- Reward/assort math must match exactly or trader economies break — port with unit-style spot checks against a known profile.
- Determinism: `generateMongoIdFromSeed` must produce identical IDs run-to-run (and ideally match the TS output) so quest links/profiles stay stable.

---

## 4a. How `MainQuests.json` was generated, and the v1 validation strategy

**Important context (from the author):** the curated list was **not** hand-typed. It was
built **programmatically**:

1. A custom **difficulty scoring** function assigned each quest a score, **per trader**.
2. Quests were **sorted by that score** (rough easy → hard ordering) within each trader.
3. Quests that worked well together were grouped into **multi-step chains** — this is why
   some `MainQuests.json` entries are **arrays of names** (parallel/sub-chain) rather than
   single strings.

**v1 strategy (agreed): "port-then-diff, then fix."**
- Carry over the existing `MainQuests.json` from the TS repo **as the baseline** (it already
  encodes the difficulty ordering + the multi-step groupings — that work is preserved).
- Run a **diff/validation pass** against the current 558-quest DB:
  - Resolve every `QuestName` → quest `_id`. Report any name that **no longer exists** or
    has changed.
  - Report current-DB quests for a trader that are **missing from the list** (new quests
    added since the list was generated).
- **Fix from there**: manually slot unmatched/new quests into the ordering (or drop renamed
  ones), keeping the existing scored order intact where possible.

**Future option (not v1):** re-implement the original difficulty-scoring algorithm in C# so
the list can be **regenerated** from any wipe's quest set automatically (instead of manual
fix-ups). Worth doing only if quest churn becomes a maintenance burden — for now the
port-then-diff approach reuses the existing curation and only patches the deltas.

---

## 5. Build phases

1. **Scaffold** — csproj (ref SPT 4.0 server assemblies like ABPS), ModMetadata, IOnLoad entry, logger, build to local TEST `SPT/user/mods/`.
2. **Config** — port `config.json` + `QuestConfigs/*.json`, typed models, loader (`ModConfig`).
3. **Adjuster** — port AdjusterModule (scalar modifiers). Testable in isolation, lowest risk → do first.
4. **Overhaul core** — remove list, flatten, QuestName↔Id map, level-99 hide, chain re-linking.
5. **Overhaul traders** — trader unlock chains, fence/kappa start requirements.
6. **Overhaul rewards** — exp/money/standing rebalance + currency normalization.
7. **Overhaul assorts** — loyalty reassignment + ammo tiers (last, hardest).
8. **Validation pass** — re-check `MainQuests.json` against current 558-quest DB; log unmatched; fix.
9. **Test** — fresh profile on TEST, walk early progression per trader, verify unlocks/rewards.

---

## 6. Open decisions (need owner input)

- **Scope v1**: ship Adjuster-only first (low-risk, useful immediately), then Overhaul? Or full parity in one go?
- **MainQuests.json**: ~~reuse vs re-curate~~ **DECIDED** → port the existing list as baseline, diff against current DB, fix the deltas (see §4a). Re-implementing the scoring algorithm in C# is a possible future enhancement, not v1.
- **Config-manager web UI**: ABPS has one (Blazor `wwwroot`); do we want runtime config for AQP or static JSON only?
- **Mod GUID / name / version / license** for `ModMetadata`.
- **Repo destiny**: stays a private nested repo, or eventually pushed to your GitHub as `AlgorithmicQuestingProgression-csharp`?

---

## 7. References
- Original TS mod: https://github.com/Andrewgdewar/AlgorithmicQuestingProgression
- Working C# reference: `D:\tarky\botplacementsystem\` (ABPS fork)
- C# examples: `D:\tarky\server-mod-examples\` (esp. 2EditDatabase, 3EditSptConfig, 5ReadCustomJsonConfig, 14AfterDBLoadHook)
- Quest data: `D:\tarky\TEST\SPT\SPT_Data\database\templates\quests.json` (558 quests)
