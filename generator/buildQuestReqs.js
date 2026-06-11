// Produce a per-trader, in-MainQuests-order breakdown of every quest's objectives
// with the exact data needed to author deleteReqList / adjustReqsList entries:
//   - condition id (for id-based delete/adjust)
//   - index (for index-based delete)
//   - conditionType, value, target item name(s), FiR, plantTime
//   - human task text from locale
//
// Output: generator/output/questReqs.json   (trader -> [ { quest, objectives:[...] } ])
//         generator/output/questReqs.txt    (readable walk-through)
// Run: node generator/buildQuestReqs.js
const fs = require("fs");
const path = require("path");
const DB = "D:/tarky/TEST/SPT/SPT_Data/database";
const CFG = path.join(__dirname, "..", "config");

const questsArr = Object.values(JSON.parse(fs.readFileSync(`${DB}/templates/quests.json`)));
const items = JSON.parse(fs.readFileSync(`${DB}/templates/items.json`));
const locale = JSON.parse(fs.readFileSync(`${DB}/locales/global/en.json`));
const mainQuests = JSON.parse(fs.readFileSync(path.join(CFG, "MainQuests.json")));

const byName = {};
for (const q of questsArr) byName[q.QuestName] = q;

function itemName(tpl) {
  if (Array.isArray(tpl)) {
    const n = tpl.slice(0, 2).map(itemName);
    return tpl.length > 2 ? n.join("/") + "/…" : n.join("/");
  }
  return locale[`${tpl} ShortName`] || locale[`${tpl} Name`] || tpl;
}

// summarize a single AvailableForFinish condition
function describe(c) {
  const o = { id: c.id, type: c.conditionType, value: c.value };
  // counter (kills / location / etc.)
  if (c.counter && c.counter.conditions) {
    const kills = c.counter.conditions.filter((i) => i.conditionType === "Kills");
    const inner = c.counter.conditions.map((i) => i.conditionType);
    o.counter = inner;
    if (kills.length) {
      o.killTargets = kills.map((k) =>
        (k.savageRole && k.savageRole.length ? k.savageRole.join("/") : k.target) || "Any"
      );
    }
  }
  if (c.target !== undefined) o.targetItem = itemName(c.target);
  if (c.onlyFoundInRaid) o.fir = true;
  if (c.plantTime != null) o.plantTime = c.plantTime;
  if (c.oneSessionOnly) o.oneSessionOnly = true;
  o.text = locale[c.id] || null;
  return o;
}

function flattenEntries(arr) {
  // returns ordered [{name, grouped:bool}]
  const out = [];
  for (const e of arr) {
    if (Array.isArray(e)) e.forEach((n, i) => out.push({ name: n, grouped: true, partOf: e[0] }));
    else out.push({ name: e, grouped: false });
  }
  return out;
}

const result = {};
const lines = [];
for (const [trader, arr] of Object.entries(mainQuests)) {
  result[trader] = [];
  lines.push(`\n========== ${trader} ==========`);
  const entries = flattenEntries(arr);
  let order = 0;
  for (const { name, grouped } of entries) {
    order++;
    const q = byName[name];
    if (!q) {
      lines.push(`\n${order}. ${name}   !!! NOT IN DB !!!`);
      result[trader].push({ order, quest: name, missing: true });
      continue;
    }
    const finish = (q.conditions && q.conditions.AvailableForFinish) || [];
    const objectives = finish.map((c, idx) => ({ index: idx, ...describe(c) }));
    result[trader].push({
      order, quest: name, id: q._id, grouped, type: q.type, objectives,
    });
    lines.push(`\n${order}. ${name}${grouped ? "  (chain)" : ""}   [${q.type}]  id=${q._id}`);
    objectives.forEach((o) => {
      const bits = [];
      if (o.value != null) bits.push(`val=${o.value}`);
      if (o.killTargets) bits.push(`kill=${o.killTargets.join(",")}`);
      if (o.targetItem) bits.push(`item=${o.targetItem}`);
      if (o.fir) bits.push("FiR");
      if (o.plantTime != null) bits.push(`plant=${o.plantTime}`);
      if (o.oneSessionOnly) bits.push("1session");
      lines.push(`     [${o.index}] ${o.type}  (${bits.join(" ")})  id=${o.id}`);
      if (o.text) lines.push(`         "${o.text}"`);
    });
  }
}

fs.writeFileSync(path.join(__dirname, "output", "questReqs.json"), JSON.stringify(result, null, 2));
fs.writeFileSync(path.join(__dirname, "output", "questReqs.txt"), lines.join("\n"));
console.log("Wrote generator/output/questReqs.json and questReqs.txt");
console.log(`Traders: ${Object.keys(result).length}, total entries: ${Object.values(result).reduce((a, b) => a + b.length, 0)}`);
