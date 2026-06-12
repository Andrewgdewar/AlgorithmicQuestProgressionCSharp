// Validates MainQuests.json against the current DB:
//  - every QuestName resolves to a quest _id  (reports stale/renamed names)
//  - every non-removed, non-event quest in the DB appears somewhere in MainQuests
//    (reports quests MISSING from the curated list — new since the list was made)
// Run: node generator/validateMainQuests.js
const fs = require("fs");
const path = require("path");
const DB = "D:/tarky/TEST/SPT/SPT_Data/database";
const CFG = path.join(__dirname, "..", "config");

const quests = Object.values(JSON.parse(fs.readFileSync(`${DB}/templates/quests.json`)));
const questConfig = JSON.parse(fs.readFileSync("D:/tarky/TEST/SPT/SPT_Data/configs/quest.json"));
const mainQuests = JSON.parse(fs.readFileSync(path.join(CFG, "MainQuests.json")));

// trader id -> nickname
const traderNames = {};
for (const dir of fs.readdirSync(`${DB}/traders`)) {
  const f = `${DB}/traders/${dir}/base.json`;
  if (fs.existsSync(f)) { try { traderNames[dir] = JSON.parse(fs.readFileSync(f)).nickname || dir; } catch {} }
}
// Map nickname -> uppercase enum key used in MainQuests
const NICK_TO_KEY = {
  Prapor: "PRAPOR", Therapist: "THERAPIST", Skier: "SKIER", Peacekeeper: "PEACEKEEPER",
  Mechanic: "MECHANIC", Ragman: "RAGMAN", Jaeger: "JAEGER", Fence: "FENCE",
  Lightkeeper: "LIGHTHOUSEKEEPER", "Lightkeeper ": "LIGHTHOUSEKEEPER", Arena: "REF",
  "БТР": "BTR", BTR: "BTR",
};

// flatten MainQuests -> list of {trader, name}
const listed = new Set();
for (const [trader, arr] of Object.entries(mainQuests)) {
  for (const entry of arr) {
    if (Array.isArray(entry)) entry.forEach((n) => listed.add(n));
    else listed.add(entry);
  }
}

const dbNames = new Set(quests.map((q) => q.QuestName));

// name -> [ {id, traderKey}, ... ]  (to detect duplicate quest names)
const nameToEntries = {};
for (const q of quests) {
  const key = NICK_TO_KEY[traderNames[q.traderId]] || traderNames[q.traderId] || q.traderId;
  (nameToEntries[q.QuestName] = nameToEntries[q.QuestName] || []).push({ id: q._id, trader: key });
}

// 1) Listed names that don't exist in the DB
const staleByTrader = {};
for (const [trader, arr] of Object.entries(mainQuests)) {
  const flat = arr.flatMap((e) => (Array.isArray(e) ? e : [e]));
  const stale = flat.filter((n) => !dbNames.has(n));
  if (stale.length) staleByTrader[trader] = stale;
}

// 2) DB quests (PMC) not in the list — excluding removeList + seasonal events
const removeList = [
  "Make Amends","Illegal Logging","Chilly","Enough Drinks for That One","Hide in Plain Sight",
  "This Is My Party","A Healthy Alternative","Kind of Sabotage","The Stylish One","Important Patient",
  "Bloodhounds","Hint","Failed Setup","Hustle","Tourists","Cocktail Tasting","Overseas Trust - Part 1",
  "Overseas Trust - Part 2","The Punisher Harvest","The Tarkov Mystery","A Key to Salvation","Import ontrol",
  "Whats Your Evidence","Caught Red-Handed","Gunsmith - Special Order","Gun Connoisseur",
  "Customer Communication","Supply and Demand","Into the Inferno","In and Out","Ours by Right",
  "Provide Cover","Cream of the Crop","Before the Rain","Night of The Cult","The Graven Image",
  "Dont Believe Your Eyes","Dirty Blood","Burn It Down","The Root Cause","Matter of Technique",
  "Find the Source","Gloves Off","Sample IV - A New Hope","Darkest Hour Is Just Before Dawn",
  "Radical Treatment","Forgotten Oaths","Global Threat","Watch the Watcher","Not a Step Back",
  "Pressured by Circumstances","Conservation Area","Contagious Beast","Gratitude",
];
const seasonalEventIds = new Set(
  Object.entries(questConfig.eventQuests || {})
    .filter(([, e]) => e.season && String(e.season).toLowerCase() !== "none")
    .map(([id]) => id)
);
const missingByTrader = {};
for (const q of quests) {
  if (listed.has(q.QuestName)) continue;
  if (removeList.includes(q.QuestName)) continue;
  if (seasonalEventIds.has(q._id)) continue;
  const key = NICK_TO_KEY[traderNames[q.traderId]] || traderNames[q.traderId] || q.traderId;
  (missingByTrader[key] = missingByTrader[key] || []).push(q.QuestName);
}

let staleCount = 0;
console.log("=== STALE names in MainQuests (not in current DB) ===");
for (const [t, arr] of Object.entries(staleByTrader)) {
  console.log(`\n${t} (${arr.length}):`);
  arr.forEach((n) => console.log("  -", n));
  staleCount += arr.length;
}
if (!staleCount) console.log("  (none)");

let missingCount = 0;
console.log("\n\n=== MISSING from MainQuests (in DB, not listed; excl. removeList + seasonal) ===");
for (const [t, arr] of Object.entries(missingByTrader)) {
  console.log(`\n${t} (${arr.length}):`);
  arr.forEach((n) => console.log("  +", n));
  missingCount += arr.length;
}
if (!missingCount) console.log("  (none)");

// 3) DUPLICATE names among the curated list — these break the chain rebuild:
//    the applier clears AvailableForStart on EVERY quest with the name, but the
//    chain rebuild only re-gates the FIRST id (name->id map), so the extra copies
//    stay ungated and become startable immediately.
let dupCount = 0;
console.log("\n\n=== DUPLICATE curated names (name -> multiple DB ids) — CHAIN BREAKER ===");
for (const name of listed) {
  const entries = nameToEntries[name];
  if (entries && entries.length > 1) {
    dupCount++;
    console.log(`\n'${name}' (${entries.length}):`);
    entries.forEach((e) => console.log(`  - ${e.id}  [${e.trader}]`));
  }
}
if (!dupCount) console.log("  (none)");

console.log(`\n\nTotals: ${staleCount} stale, ${missingCount} missing, ${dupCount} duplicate. Listed: ${listed.size}, DB: ${dbNames.size}`);

