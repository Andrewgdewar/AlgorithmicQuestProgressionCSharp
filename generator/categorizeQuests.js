// Categorize quests by the curation rules: currency-handover, loyalty/standing gate,
// Labyrinth (endgame), and Christmas/event. Helps decide RemoveList additions and
// difficulty re-scoring. Uses authoritative locale task text.
// Run: node generator/categorizeQuests.js
const fs = require("fs");
const path = require("path");
const DB = "D:/tarky/TEST/SPT/SPT_Data/database";

const quests = Object.values(JSON.parse(fs.readFileSync(`${DB}/templates/quests.json`)));
// english locale for task text
const locale = JSON.parse(fs.readFileSync(`${DB}/locales/global/en.json`));

function taskText(q) {
  const conds = (q.conditions && q.conditions.AvailableForFinish) || [];
  const lines = [];
  for (const c of conds) {
    const key = c.id;
    const txt = locale[key];
    if (txt) lines.push(txt);
  }
  return lines.join(" | ");
}

const result = { currency: [], loyalty: [], labyrinth: [], christmas: [] };

const CUR = /\b(RUB|EUR|USD|GP|Roubles|Euros|Dollars)\b/i;
const HANDOVER_CUR = /hand over[^|]*\b(RUB|EUR|USD|GP|Roubles|Euros|Dollars)\b/i;
const LOYALTY = /(reach (level )?\d.*loyalty|reach [\d.]+ standing|loyalty level)/i;
const LABY = /(Labyrinth|Minotaur)/i;
const XMAS = /(Christmas|New Year|Santa|holiday)/i;

for (const q of quests) {
  const name = q.QuestName;
  const text = taskText(q);
  if (HANDOVER_CUR.test(text)) result.currency.push(name);
  if (LOYALTY.test(text)) result.loyalty.push(name);
  if (LABY.test(text)) result.labyrinth.push(name);
  if (XMAS.test(text)) result.christmas.push(name);
}

for (const k of Object.keys(result)) {
  console.log(`\n=== ${k.toUpperCase()} (${result[k].length}) ===`);
  result[k].sort().forEach((n) => console.log("  " + n));
}
fs.writeFileSync(
  path.join(__dirname, "output", "categorizedQuests.json"),
  JSON.stringify(result, null, 2)
);
console.log("\nWrote generator/output/categorizedQuests.json");
