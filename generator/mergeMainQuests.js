// Auto-merge missing quests into MainQuests.json, preserving the existing curated
// order and KEEPING QUEST CHAINS GROUPED (multi-part series stay in one embedded array).
//
//  - fixes the 6 stale renames (… -> "… PVE ZONE")
//  - drops the REF key entirely (Arena PvP quests are owned by the RefModule)
//  - maps Lightkeeper (nickname "caretaker") + БТР correctly
//  - for each trader, finds DB quests missing from the list (excl. removeList + seasonal
//    + Arena), groups multi-part series together, and inserts each group/single at its
//    difficulty-score position WITHOUT reordering the existing entries
//  - if a missing quest belongs to a series already present in the list, it JOINS that
//    existing group instead of forming a new one (never splits a chain)
//
// Output: config/MainQuests.merged.json  (review, then rename over MainQuests.json)
// Run: node generator/mergeMainQuests.js
const fs = require("fs");
const path = require("path");
const DB = "D:/tarky/TEST/SPT/SPT_Data/database";
const CFG = path.join(__dirname, "..", "config");

const quests = Object.values(JSON.parse(fs.readFileSync(`${DB}/templates/quests.json`)));
const questConfig = JSON.parse(fs.readFileSync("D:/tarky/TEST/SPT/SPT_Data/configs/quest.json"));
const mainQuests = JSON.parse(fs.readFileSync(path.join(CFG, "MainQuests.json")));
const flat = JSON.parse(fs.readFileSync(path.join(__dirname, "output", "questDifficulty.flat.json")));

const score = {};
flat.forEach((q) => (score[q.questName] = q.score));

const removeList = new Set([
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
  "Pressured by Circumstances","Conservation Area","Contagious Beast",
]);

// trader id -> nickname -> MainQuests key
const traderNames = {};
for (const dir of fs.readdirSync(`${DB}/traders`)) {
  const f = `${DB}/traders/${dir}/base.json`;
  if (fs.existsSync(f)) { try { traderNames[dir] = JSON.parse(fs.readFileSync(f)).nickname || dir; } catch {} }
}
const NICK_TO_KEY = {
  Prapor: "PRAPOR", Therapist: "THERAPIST", Skier: "SKIER", Peacekeeper: "PEACEKEEPER",
  Mechanic: "MECHANIC", Ragman: "RAGMAN", Jaeger: "JAEGER", Fence: "FENCE",
  Lightkeeper: "LIGHTHOUSEKEEPER", Arena: "REF", "БТР": "BTR", BTR: "BTR", caretaker: "LIGHTHOUSEKEEPER",
};
const ARENA_TRADER_ID = "6617beeaa9cfa777ca915b7c";

// stale rename fixes (old name in list -> new name in DB)
const RENAMES = {
  "Easy Money - Part 1": "Easy Money - Part 1 PVE ZONE",
  // REF entries are dropped entirely, no need to rename
};

// --- series stem: text before " - Part N" (else the whole name) ---
function stem(name) {
  const m = name.match(/^(.*?) - Part \d+/);
  return m ? m[1] : name;
}
function partNum(name) {
  const m = name.match(/ - Part (\d+)/);
  return m ? parseInt(m[1], 10) : 0;
}

const dbNames = new Set(quests.map((q) => q.QuestName));

// 1) apply renames + drop REF
const out = {};
for (const [trader, arr] of Object.entries(mainQuests)) {
  if (trader === "REF") continue; // RefModule owns Arena
  out[trader] = arr.map((entry) =>
    Array.isArray(entry) ? entry.map((n) => RENAMES[n] || n) : (RENAMES[entry] || entry)
  );
}

// 2) build set of listed names + a stem -> entry-index map per trader
function listedNames(arr) {
  const s = new Set();
  arr.forEach((e) => (Array.isArray(e) ? e.forEach((n) => s.add(n)) : s.add(e)));
  return s;
}

// 3) gather missing quests per trader-key, grouped by series stem
const seasonalEventIds = new Set(
  Object.entries(questConfig.eventQuests || {})
    .filter(([, e]) => e.season && String(e.season).toLowerCase() !== "none")
    .map(([id]) => id)
);
const allListed = new Set();
Object.values(out).forEach((arr) => listedNames(arr).forEach((n) => allListed.add(n)));

const missingByKey = {}; // key -> [names]
for (const q of quests) {
  const name = q.QuestName;
  if (allListed.has(name)) continue;
  if (removeList.has(name)) continue;
  if (seasonalEventIds.has(q._id)) continue;
  if (q.traderId === ARENA_TRADER_ID) continue; // Arena -> RefModule
  const key = NICK_TO_KEY[traderNames[q.traderId]] || traderNames[q.traderId] || q.traderId;
  (missingByKey[key] = missingByKey[key] || []).push(name);
}

function entryScore(entry) {
  const names = Array.isArray(entry) ? entry : [entry];
  const scores = names.map((n) => score[n]).filter((s) => s != null);
  return scores.length ? Math.min(...scores) : Infinity;
}

const report = {};
for (const [key, names] of Object.entries(missingByKey)) {
  if (!out[key]) out[key] = [];
  const list = out[key];

  // group missing by stem
  const groups = {};
  for (const n of names) {
    const s = stem(n);
    (groups[s] = groups[s] || []).push(n);
  }

  const added = [];
  for (const [s, members] of Object.entries(groups)) {
    members.sort((a, b) => partNum(a) - partNum(b));

    // does this series already exist in the list? (single or array) -> join it
    let joined = false;
    for (let i = 0; i < list.length; i++) {
      const entry = list[i];
      const entryNames = Array.isArray(entry) ? entry : [entry];
      if (entryNames.some((en) => stem(en) === s)) {
        const merged = [...entryNames, ...members].sort((a, b) => partNum(a) - partNum(b));
        list[i] = merged;
        joined = true;
        added.push(`${members.join(", ")} -> joined existing "${s}" group`);
        break;
      }
    }
    if (joined) continue;

    // else insert as a new entry (array if multi-part, else string) at score position
    const newEntry = members.length > 1 ? members : members[0];
    const sc = entryScore(newEntry);
    let insertAt = list.length;
    for (let i = 0; i < list.length; i++) {
      if (entryScore(list[i]) > sc) { insertAt = i; break; }
    }
    list.splice(insertAt, 0, newEntry);
    added.push(`${members.join(", ")} -> inserted @${insertAt} (score ${sc})`);
  }
  report[key] = added;
}

// write merged + report
fs.writeFileSync(path.join(CFG, "MainQuests.merged.json"), JSON.stringify(out, null, 2));

console.log("=== MERGE REPORT ===");
for (const [key, added] of Object.entries(report)) {
  console.log(`\n${key} (+${added.length}):`);
  added.forEach((a) => console.log("  " + a));
}
console.log("\nWrote config/MainQuests.merged.json");
