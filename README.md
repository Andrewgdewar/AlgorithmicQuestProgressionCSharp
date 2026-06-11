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

### C. NEW — Remove map-transit requirements (`removeTransitQuests`)
Not in the original TS mod. **Inspired by [Lacyway/LacywayPvETweaks](https://github.com/Lacyway/LacywayPvETweaks) `EditTransits()`** — strips the "transit between maps" requirements that are tedious in a co-op PvE server. Behaves as a standalone toggle (like the Adjuster modifiers) so it works with or without the Overhaul.

Lacy's algorithm (the reference we'll port), per quest in `databaseService.GetQuests()`:
1. **Find transit quests** — any quest whose `Conditions.AvailableForFinish` has a `Counter.Conditions` entry where `Status.Count == 1 && Status.Contains("Transit")`.
2. For each, locate that transit condition.
3. For sibling `AvailableForFinish` conditions flagged `OneSessionOnly` → set `OneSessionOnly = false` and queue their locale Ids for cleanup. If the transit condition itself is `OneSessionOnly`, queue **all** of that quest's `AvailableForFinish` Ids.
4. **Remove** the transit condition from `AvailableForFinish`.
5. **Strip dangling `VisibilityConditions`** — any child `AvailableForFinish` condition whose `VisibilityConditions[].Target` pointed at the removed transit step gets that visibility link removed (so the step shows immediately instead of being gated behind a now-deleted transit).
6. **Locale cleanup** — via `Locales.Global` lazy-load transformers, strip the trailing `" (MapName)"` suffix from the affected condition descriptions (`LastIndexOf(" (")`).

> **Decision (owner):** Lacy's mod will have its `transits` toggle **turned off**; AQP owns transit removal. Build it in (`removeTransitQuests`, default **on**).

### D. NEW — PvE-friendly Ref / Arena quests (`refChanges`)
Not in the original TS mod. **Ported from [Lacyway/LacywayPvETweaks](https://github.com/Lacyway/LacywayPvETweaks) `EditRef()`** — rewrites the questline so it's completable on a co-op PvE server. Standalone toggle.

> **⚠️ Terminology: Ref === Arena.** The trader Lacy calls "Ref" has the in-game **nickname "Arena"** (trader id `6617beeaa9cfa777ca915b7c`). Its quests are the **Arena PvP-mode** tasks ("To Great Heights", "Against the Conscience", "Balancing", "Create a Distraction", "Decisions Decisions", "Provide Viewership", etc.) — objectives like *"Win a match in CheckPoint/LastHero mode in Arena"*. Arena is BSG's separate PvP mode and is **annoying/unplayable in co-op PvE**, so these `... PVE ZONE` quests must be rewritten into normal raid objectives. This is the same fix as Ref — same trader, just the Arena-mode questline.

Lacy's algorithm (the reference we'll port) — clears each target quest's `Conditions.AvailableForFinish` and re-adds PvE-doable `CounterCreator` / `HandoverItem` conditions (eliminate Scavs/PMCs/USEC/BEAR, hand over FiR dogtags, kill the Goons in one raid, kill Partisan), each with a stable `Id` + matching **locale** string applied via `Locales.Global` lazy-load transformers.

Lacy's `refQuests` ids resolve to the Arena trader's "To Great Heights - Part 1..5" line (verified against the current DB: ids `68341eb2…`, `68341f6f…`, `6834202a…`, `68342151…`, `68342265…`). We'll port the same approach and extend it to cover **all 15 Arena quests** (the full `PVE ZONE` set), since every one is a PvP-mode task in raw data.

> **Decision (owner):** build Ref/Arena-quest tweaks into AQP (`refChanges`, default **on**), mirroring Lacy's behavior. If Lacy's mod stays installed, turn **off** its `refChanges` so only AQP edits this trader. The 15 Arena quests are listed for reference in `generator/output/questDifficulty.json` under the `"Arena"` trader.

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

## 2b. Quest data reference (current DB — 558 quests)

Enumerated from `quests.json` so the difficulty model + ports target real shapes. A quest is
`Dictionary<string, IQuest>` keyed by `_id`; each `IQuest` has `QuestName`, `TraderId`, `Side`
(all 558 are `Pmc`), `Type`, and `Conditions.{AvailableForStart, AvailableForFinish, Fail}` +
`Rewards.{Started, Success, Fail}`.

**`quest.Type`** (12 values): `PickUp` 136, `Elimination` 132, `Completion` 119, `Exploration`
59, `Discover` 48, `WeaponAssembly` 28, `Multi` 12, `Skill` 9, `Loyalty` 7, `Merchant` 6,
`Experience` 1, `Standing` 1.

**`AvailableForStart.ConditionType`** (the gate types): `Quest` 736, `Level` 224,
`TraderStanding` 8, `TraderLoyalty` 4.

**`AvailableForFinish.ConditionType`** (the objective types): `CounterCreator` 609,
`HandoverItem` 413, `FindItem` 269, `LeaveItemAtLocation` 149, `PlaceBeacon` 96,
`WeaponAssembly` 32, `TraderLoyalty` 10, `Skill` 10, `Quest` 7, `SellItemToTrader` 5,
`GlobalVariableValue` 3, `TraderStanding` 1, `VisitPlace` 1, `HideoutArea` 1.

**`CounterCreator.Counter.Conditions.ConditionType`** (what a "counter" objective counts):
`Kills` 275, `VisitPlace` 212, `Location` 203, `ExitStatus` 103, `InZone` 32, `Equipment` 24,
`ExitName` 12, `LaunchFlare` 8, `ArenaGameMode` 6, `ArenaMatchPlace` 6, `HealthEffect` 5,
`Shots` 2, `HealthBuff` 2, `ArenaPlayerInTeamPlace` 1, `Time` 1, `UnderArtilleryFire` 1.

**`Rewards.Success.Type`**: `Item` 1472, `TraderStanding` 532, `Experience` 508,
`AssortmentUnlock` 231, `Skill` 83, `ProductionScheme` 31, `CustomizationDirect` 11,
`Achievement` 10, `NotificationPopup` 3, `TraderUnlock` 2, `WebPromoCode` 1, `Pockets` 1,
`TraderStandingRestore` 1.

> **Difficulty-model signals (from this distribution):** the meaningful objective difficulty
> lives in `CounterCreator` (kills/location/exit-status counts + the `Value`), `HandoverItem`/
> `FindItem` (item count + whether `OnlyFoundInRaid` + whether it's a real `QuestItem`),
> `LeaveItemAtLocation`/`PlaceBeacon` (plant time + map exposure), and the **start-gate Level**.
> Reward magnitude (`Experience`, `TraderStanding`, money `Item`) is a secondary proxy BSG
> already tuned to difficulty — useful as a tie-breaker/sanity signal. See §4b.

---

## 2c. Porting gotchas (mined from the TS source — keep these in the C# port)

The original TS handles many edge cases that are easy to miss. Each must be preserved:

**AdjusterModule**
- **Guard on objectives**: only touch a quest if `AvailableForFinish?.Count > 0` (skip empty/started-reward-only quests).
- **Level scale fallback**: `round(value * mod) || 1` and `Number(value) || 1` — never let a requirement become `0`/`NaN`.
- **No-op short-circuit**: every modifier checks `== 1` and breaks first (avoids needless rewrites + float drift).
- **Don't scale single-target counters**: `CounterCreator` with `Value == 1` is skipped (a "kill 1 boss" must stay 1).
- **⚠️ TS fall-through bug**: in the TS `switch`, `case "CounterCreator"` has **no `break`** before `HandoverItem`/`FindItem`, so kill-count quests also run the find-item logic. In C# decide deliberately — almost certainly **add the `break`** (port the *intent*, not the bug).
- **Never reduce real quest-items**: in `HandoverItem`/`FindItem`, if `items[target]?._props?.QuestItem` is true, skip — scaling a story item count breaks the quest.
- **`Target` is string OR array**: normalize `target?[0] ?? target` before lookups.
- **Reward ITEM scaling**: skip if `items.Count > 1` or no `items[0]`, skip if `value == 1`; when scaling, update **both** `value` and `items[0].upd.StackObjectsCount`.
- **Trader-standing rounding**: snap to 0.05 increments: `round((value/0.05) * mod * 0.05 * 100)/100`.
- **Gunsmith replacement**: convert `WeaponAssembly → Elimination`, rebuild a kill condition, and rewrite locales for **every** language (`languages` keys), with `localeConfig[lang] ?? localeConfig.en` fallback and `<weapon>`/`<number>` placeholder substitution; handle `target` string-vs-array for the weapon name lookup.

**OverhaulModule / transforms**
- **Deterministic ids**: `generateMongoIdFromSeed` = MD5(seed) hex, take first 24 chars, `padEnd(24,'0')`. Must be byte-identical run-to-run (and ideally match the TS output) so quest links and profiles stay stable.
- **Reverse-iterate + `seen` set** when building `QuestName → _id` so duplicate names resolve deterministically (first occurrence wins after the reverse).
- **MainQuests entries are string OR array** — arrays are parallel sub-chains, linked with `quantity = 1`; the main list links with `TraderQuestProgressionQuantity[trader]`.
- **Chain link uses `status: [4]`** (Success) on the `Quest` start-condition (vanilla often uses `[4,5]`).
- **`IterateOverArrayAddingQuestReqs`**: chunk the ordered list into sets of `quantity`, and make every quest in set *n* require **all** quests in set *n-1*.
- **Currency normalization**: each trader has a native currency (`TraderCurrencies`: Peacekeeper = USD, Ref = GP, rest = Roubles). Filter out money rewards not in the native currency and convert via `convertMoney` (static rates: USD≈160, EUR≈180, GP≈20000 roubles; convert through roubles, round, reject negatives/invalid).
- **Ref is special-cased** (few quests): standing reward `= (index+1)/10`, money `= value + index * refMoneyMultiplier` (+ matching `StackObjectsCount`).
- **Sort reward pools ascending** by value before index-assigning them along the chain.
- **Assort reassignment**: strip the old `AssortmentUnlock` reward off each source quest first, then redistribute via `assignQuestNamesWithWeight` (weight 0..1, skew across the chain; Peacekeeper has *more* unlocks than quests — ~0.91 ratio — so guard the smoothing), plus ammo-tier unlocks keyed by `Caliber`.
- **`weightFactor` must be 0..1** — throw/clamp otherwise.

---

## 3. Proposed architecture (C#)

Kept **minimal**, modeled on `server-mod-examples/5ReadCustomJsonConfig` and Lacy's mod (a flat
project: one entry file + one file per concern + a `config/` folder of JSON). No `Server/`
sub-project, no `Controllers/Globals/Models` layering, no web UI — that's ABPS overkill we don't
need for a static-JSON DB-editing mod.

```
AlgorithmicQuestingProgression-csharp/
  AlgorithmicQuestingProgression.csproj   # Sdk.Web, 3 SPTarkov pkg refs (Common/DI/Server.Core)
  AlgorithmicQuestingProgression.cs       # ModMetadata record + IOnLoad entry (dispatches modules by toggle)
  AdjusterModule.cs                        # port of AdjusterModule (scalar modifiers)
  TransitModule.cs                         # NEW — remove map-transit requirements (ports Lacy EditTransits)
      RefModule.cs                             # NEW — PvE-friendly Ref/Arena questline (ports Lacy EditRef)
  OverhaulModule.cs                        # port of OverHaulModule (the big quest-tree restructure)
  ModConfig.cs                             # typed config records (toggles, modifiers, adjustments, main quests)
  Utils.cs                                 # MongoId-from-seed, QuestName↔Id helpers, removeList constants
  config/
    config.json                            # module toggles + scalar modifiers
    MainQuests.json                        # generated list (carry over from TS repo, diff vs 558-quest DB — see §4a)
    questAdjustments.json                  # deleteReqList/adjustReqsList/TraderUnlockQuests/etc.
    ammoLevelUnlocks.json                  # calibre → ammo unlock tiers
  LICENSE                                  # MIT
  .gitignore
  README.md  (this file)
  .git/      (own repository)
```

Config is read with `ModHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly())` +
`ModHelper.GetJsonDataFromFile<T>(path, "config/config.json")`. JSON files are copied to output
via `<None Update="config\*.json"><CopyToOutputDirectory>PreserveNewest</...></None>`.

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
| Transit removal (Lacy port) | Easy–Med | Find Transit-status conditions, remove + fix VisibilityConditions + locale suffix cleanup |
| Ref quest tweaks (Lacy port) | Med | Rebuild Ref/Arena quests' conditions + hard-coded ids + locales; Arena (PvP-mode) trader, 15 quests; mostly transcription |
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

**Decision (owner): also recreate the difficulty-scoring generator (§4b).** Rather than only
patching a stale list by hand, we will re-implement a per-trader difficulty score so the
ordering can be **regenerated** from any wipe's quest set. The baseline list still serves as a
sanity-check/diff target, but the generator becomes the source of truth for ordering.

---

## 4b. Difficulty-scoring model (to recreate the generator)

Goal: assign every quest a per-trader difficulty score, sort ascending (easy → hard) within
each trader, then group adjacent quests into multi-step chains. Score is built from the data
signals enumerated in §2b. Proposed weighted model (all weights live in config so we can tune):

**Primary signal — start gate**
- `Level` start-requirement value → strong difficulty proxy (BSG already gates by level).

**Objective signals (`AvailableForFinish`)**
- `CounterCreator` →
  - `Kills`: `Value` × (target weight). Boss/Goons/PMC targets weigh more than `Savage`/`Any`;
    `SavageRole` boss tags bump it. Single-target (`Value == 1`) bosses are still "hard".
  - `Location` / `VisitPlace` / `InZone` / `ExitName` / `ExitStatus`: small map-knowledge cost.
  - `Shots`/`HealthEffect`/`Equipment`/`UnderArtilleryFire`/etc.: minor situational difficulty.
- `HandoverItem` / `FindItem`: `Value` × rarity, **bigger** if `OnlyFoundInRaid`, and **much**
  bigger if it's a real `QuestItem` (must be found on a map). Skip-list barter junk weighs ~0.
- `LeaveItemAtLocation` / `PlaceBeacon`: plant action under fire → moderate + `plantTime` factor.
- `WeaponAssembly`: flat moderate (gunsmith) — and note these get replaced if `replaceGunsmith`.
- `Skill` / `TraderLoyalty` / `SellItemToTrader`: grind cost, low-moderate.

**Secondary signal — reward magnitude (tie-breaker / sanity check, low weight)**
- `Experience` + money (`Item` reward in trader currency) + `TraderStanding`: BSG scales these
  with difficulty, so use as a smoothing/tie-break term, not a primary driver.

**Aggregation**
- `score(quest) = wLevel*level + Σ objectiveScores + wReward*normalizedReward`.
- Sort each trader's quests by `score` ascending. Stable-sort with a tie-break on level then
  reward so output is deterministic.
- **Chaining**: walk the sorted list and merge runs that are clearly the same arc (shared
  name stem like "Part N", or same target item/location) into arrays — reproducing the
  multi-step groups. Keep chain size small (the TS linked sub-chains with `quantity = 1`).

**Validation**: regenerate, then diff against the carried-over baseline `MainQuests.json` to
confirm the ordering is sane (large reorderings get eyeballed). Tunable weights mean we can
match the old feel without hand-editing every wipe.

### Implementation: `generator/generateQuestDifficulty.js`

A standalone, dependency-free **Node script** (NOT run on game start) implements the model
above by reading the TEST server's `SPT_Data` JSON directly
(`quests.json` + `items.json` + `locales/global/en.json` + `traders/*/base.json`).

- All knobs live in a `WEIGHTS` block at the top — tweak + re-run to discuss values.
- Money hand-overs (e.g. "give 1,000,000 RUB") are scored by rouble-normalized amount, not
  item count; objective counts are clamped (`maxObjectiveCount`) so sentinel values like
  `9,999,999,999` (repeatable/"endless") don't blow up the score.
- Run: `node generator/generateQuestDifficulty.js`
- Outputs (committed, for tweaking/discussion):
  - `generator/output/questDifficulty.json` — `{ traderName: { questName: { order, score, level, type, reqs, rewards, id } } }`, each trader sorted easy → hard.
  - `generator/output/questDifficulty.flat.json` — flat array across all traders.
- `reqs` is a one-line human summary (e.g. `Kill 5 Scavs; Hand over 2x MP-133`).

This data is the raw material for hand-building the eventual `MainQuests` quest config.

---

## 5. Build phases

1. **Scaffold** — csproj (ref SPT 4.0 server assemblies like ABPS), ModMetadata, IOnLoad entry, logger, build to local TEST `SPT/user/mods/`.
2. **Config** — port `config.json` + `QuestConfigs/*.json`, typed models, loader (`ModConfig`).
3. **Adjuster** — port AdjusterModule (scalar modifiers). Testable in isolation, lowest risk → do first.
4. **Transit removal** — port Lacy's `EditTransits()` as a standalone toggle (low risk, independent of Overhaul).
5. **Ref quests** — port Lacy's `EditRef()` as a standalone toggle (low risk, independent of Overhaul). Targets the Ref/**Arena** trader (PvP-mode quests).
6. **Overhaul core** — remove list, flatten, QuestName↔Id map, level-99 hide, chain re-linking.
7. **Overhaul traders** — trader unlock chains, fence/kappa start requirements.
8. **Overhaul rewards** — exp/money/standing rebalance + currency normalization.
9. **Overhaul assorts** — loyalty reassignment + ammo tiers (last, hardest).
10. **Validation pass** — re-check `MainQuests.json` against current 558-quest DB; log unmatched; fix.
11. **Test** — fresh profile on TEST, walk early progression per trader, verify unlocks/rewards.

---

## 6. Decisions (resolved)

- **Scope v1**: **incremental** — ship the lower-risk standalone pieces first (Adjuster + Transit + Ref), then add the Overhaul. (Scope just meant release sequencing, not which features exist.)
- **MainQuests.json**: port the existing list as baseline, diff against current DB, fix the deltas (see §4a). Re-implementing the scoring algorithm in C# is a possible future enhancement, not v1.
- **Config-manager web UI**: **static JSON only** (no Blazor web UI).
- **Transit removal**: turn **off** Lacy's `transits`; AQP owns it (§1C), default on.
- **Ref quests**: build PvE Ref tweaks into AQP (§1D), default on; turn **off** Lacy's `refChanges` if Lacy stays installed.
- **Repo destiny**: **public** GitHub repo (eventually pushed as `AlgorithmicQuestingProgression-csharp`).

### Mod metadata (`ModMetadata`)
Following the previous mod + ABPS conventions:

| Field | Value |
|---|---|
| `ModGuid` | `com.dewardiandev.algorithmicquestingprogression` |
| Name | `AlgorithmicQuestingProgression` (same as the TS mod) |
| `Author` | `DewardianDev` |
| Version | `2.0.0` (next major after the TS mod's `1.0.2`) |
| `SptVersion` | `~4.0.x` (match the running server, e.g. ABPS uses `~4.0.3`) |
| `License` | `MIT` (same as the TS mod) |

---

## 7. References
- Original TS mod: https://github.com/Andrewgdewar/AlgorithmicQuestingProgression
- Transit-removal reference: https://github.com/Lacyway/LacywayPvETweaks (`Code/LacyPvETweaks.cs` → `EditTransits()`, `Code/TweaksConfig.cs` → `RemoveTransitQuests`)
- Ref-quest reference: https://github.com/Lacyway/LacywayPvETweaks (`Code/LacyPvETweaks.cs` → `EditRef()`, `Code/TweaksConfig.cs` → `RefChanges`). Note: Lacy's "Ref" trader = in-game nickname **"Arena"** (id `6617beeaa9cfa777ca915b7c`), the PvP-mode questline.
- Working C# reference: `D:\tarky\botplacementsystem\` (ABPS fork)
- C# examples: `D:\tarky\server-mod-examples\` (esp. 2EditDatabase, 3EditSptConfig, 5ReadCustomJsonConfig, 14AfterDBLoadHook)
- Quest data: `D:\tarky\TEST\SPT\SPT_Data\database\templates\quests.json` (558 quests)
