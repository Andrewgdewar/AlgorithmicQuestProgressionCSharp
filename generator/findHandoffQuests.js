// Detect ALL cross-trader quest dependencies in the curated MainQuests set.
//
// The per-trader linear model breaks whenever a quest owned by trader Y can only
// progress because of a quest owned by a DIFFERENT trader X. That happens two ways:
//
//   (A) ITEM FLOW   - Y consumes an item (HandoverItem / LeaveItemAtLocation /
//                     PlaceBeacon / WeaponAssembly) whose only realistic source is a
//                     FindItem / reward in X's quest. QUEST ITEMS are hard blocks
//                     (can't buy/loot them); normal items are soft (buyable) and
//                     reported separately.
//
//   (B) QUEST LINK  - Y has an AvailableForStart / AvailableForFinish / AvailableForFail
//                     condition of type "Quest" pointing at a quest owned by X.
//
// Output: generator/output/crossTrader.txt + crossTrader.json
// Run: node generator/findHandoffQuests.js
const fs = require("fs");
const path = require("path");
const DB = "D:/tarky/TEST/SPT/SPT_Data/database";
const CFG = path.join(__dirname, "..", "config");

const questsArr = Object.values(JSON.parse(fs.readFileSync(`${DB}/templates/quests.json`)));
const items = JSON.parse(fs.readFileSync(`${DB}/templates/items.json`));
const locale = JSON.parse(fs.readFileSync(`${DB}/locales/global/en.json`));
const mainQuests = JSON.parse(fs.readFileSync(path.join(CFG, "MainQuests.json")));

const TRADERS = {
  "54cb50c76803fa8b248b4571": "PRAPOR",
  "54cb57776803fa99248b456e": "THERAPIST",
  "58330581ace78e27b8b10cee": "SKIER",
  "5935c25fb3acc3127c3d8cd9": "PEACEKEEPER",
  "5a7c2eca46aef81a7ca2145d": "MECHANIC",
  "5ac3b934156ae10c4430e83c": "RAGMAN",
  "5c0647fdd443bc2504c2d371": "JAEGER",
  "579dc571d53a0658a154fbec": "FENCE",
  "638f541a29ffd1183d187f57": "LIGHTHOUSEKEEPER",
  "6617beeaa9cfa777ca915b7c": "REF",
  "656f0f98d80a697f855d34b1": "BTR",
};

const CONSUME_TYPES = new Set(["HandoverItem", "LeaveItemAtLocation", "PlaceBeacon", "WeaponAssembly"]);

const itemName = (tpl) => locale[`${tpl} ShortName`] || locale[`${tpl} Name`] || tpl;
const isQuestItem = (tpl) => !!(items[tpl] && items[tpl]._props && items[tpl]._props.QuestItem === true);
const ownerOf = (q) => TRADERS[q.traderId || q.trader] || (q.traderId || q.trader || "?");
const targets = (t) => (t == null ? [] : Array.isArray(t) ? t.flat(Infinity) : [t]);

// ---- index ALL quests in the game ----
const byName = {};
const byId = {};
const findsIndex = {};   // tpl -> Set(questName) that FIND it
const rewardIndex = {};  // tpl -> Set(questName) that REWARD it
for (const q of questsArr) {
  byName[q.QuestName] = q;
  byId[q._id] = q;
  const finish = (q.conditions && q.conditions.AvailableForFinish) || [];
  for (const c of finish) {
    if (c.conditionType === "FindItem") for (const tpl of targets(c.target)) (findsIndex[tpl] ||= new Set()).add(q.QuestName);
  }
  const succ = (q.rewards && q.rewards.Success) || (q.Rewards && q.Rewards.Success) || [];
  for (const r of succ) if (r.items) for (const it of r.items) if (it._tpl) (rewardIndex[it._tpl] ||= new Set()).add(q.QuestName);
}

// ---- curated set + which trader each curated quest belongs to ----
const flatten = (arr) => arr.flatMap((e) => (Array.isArray(e) ? e : [e]));
const curatedTraderOf = {};
for (const [trader, arr] of Object.entries(mainQuests)) for (const n of flatten(arr)) curatedTraderOf[n] = trader;
const curatedSet = new Set(Object.keys(curatedTraderOf));

// ---- analyze ----
const itemHits = [];
const linkHits = [];

for (const [trader, arr] of Object.entries(mainQuests)) {
  for (const name of flatten(arr)) {
    const q = byName[name];
    if (!q) continue;
    const C = q.conditions || {};
    const finish = C.AvailableForFinish || [];

    // ---- (A) item flow ----
    const selfFinds = new Set();
    for (const c of finish) if (c.conditionType === "FindItem") for (const tpl of targets(c.target)) selfFinds.add(tpl);

    for (const c of finish) {
      if (!CONSUME_TYPES.has(c.conditionType)) continue;
      for (const tpl of targets(c.target)) {
        if (selfFinds.has(tpl)) continue; // self-contained fetch -> fine
        const finders = [...(findsIndex[tpl] || [])].filter((n) => n !== name);
        const rewarders = [...(rewardIndex[tpl] || [])].filter((n) => n !== name);
        const quest = isQuestItem(tpl);
        if (!quest && finders.length === 0 && rewarders.length === 0) continue; // plain buyable loot
        const providers = [...new Set([...finders, ...rewarders])].map((p) => ({
          quest: p, trader: curatedTraderOf[p] || (byName[p] ? "(not curated)" : "?"),
          via: finders.includes(p) ? "find" : "reward",
        }));
        const crossTrader = providers.some((p) => p.trader !== trader && p.trader !== "(not curated)" && p.trader !== "?");
        itemHits.push({ trader, quest: name, condType: c.conditionType, item: itemName(tpl), tpl, isQuestItem: quest, providers, crossTrader });
      }
    }

    // ---- (B) quest-link ----
    for (const section of ["AvailableForStart", "AvailableForFinish", "AvailableForFail"]) {
      for (const c of C[section] || []) {
        if (c.conditionType !== "Quest") continue;
        for (const lid of targets(c.target)) {
          const lq = byId[lid];
          if (!lq) continue;
          const linkedTrader = ownerOf(lq);
          const inCurated = curatedSet.has(lq.QuestName);
          const crossTrader = linkedTrader !== trader;
          linkHits.push({ trader, quest: name, section, linkedQuest: lq.QuestName, linkedTrader, inCurated, crossTrader });
        }
      }
    }
  }
}

// ---- report ----
const L = [];
const itemCross = itemHits.filter((h) => h.crossTrader);
const itemCrossQI = itemCross.filter((h) => h.isQuestItem);
const linkCross = linkHits.filter((h) => h.crossTrader);

L.push(`Curated quests scanned: ${curatedSet.size}`);
L.push(`(A) item-flow cross-trader hits: ${itemCross.length}  (of which QUEST ITEMS = hard block: ${itemCrossQI.length})`);
L.push(`(B) quest-link cross-trader hits: ${linkCross.length}`);

L.push(`\n${"=".repeat(64)}\n(A1) HARD BLOCKS - cross-trader QUEST-ITEM flow (cannot buy/loot)\n${"=".repeat(64)}`);
for (const h of itemCrossQI) {
  L.push(`\n[${h.trader}] ${h.quest}  -- ${h.condType} ${h.item} (QUEST ITEM)`);
  for (const p of h.providers) L.push(`     <- provided by [${p.trader}] ${p.quest} (${p.via})${p.trader !== h.trader ? "  <-- DIFFERENT TRADER" : ""}`);
}

L.push(`\n${"=".repeat(64)}\n(A2) SOFT - cross-trader normal-item flow (item is buyable/lootable)\n${"=".repeat(64)}`);
for (const h of itemCross.filter((x) => !x.isQuestItem)) {
  L.push(`\n[${h.trader}] ${h.quest}  -- ${h.condType} ${h.item}`);
  for (const p of h.providers.filter((p) => p.trader !== h.trader && p.trader !== "(not curated)" && p.trader !== "?"))
    L.push(`     also found/rewarded by [${p.trader}] ${p.quest} (${p.via})`);
}

L.push(`\n${"=".repeat(64)}\n(B) QUEST-LINK cross-trader (Quest-type condition -> different trader)\n${"=".repeat(64)}`);
for (const h of linkCross) {
  L.push(`[${h.trader}] ${h.quest}  (${h.section})  ->  [${h.linkedTrader}] ${h.linkedQuest}${h.inCurated ? "" : "  (linked quest NOT in curated set)"}`);
}

fs.writeFileSync(path.join(__dirname, "output", "crossTrader.json"), JSON.stringify({ itemHits, linkHits }, null, 2));
fs.writeFileSync(path.join(__dirname, "output", "crossTrader.txt"), L.join("\n"));
console.log(L.slice(0, 3).join("\n"));
console.log("\nWrote generator/output/crossTrader.{json,txt}");
