// Find skill-leveling / XP-grind quests (e.g. "Athlete" = level Endurance+Strength).
// Detects by AvailableForFinish condition type "Level"/"Skill"-style + locale text
// "Reach <skill> skill level N" / "Reach the required <skill> skill level".
// Run: node generator/findSkillQuests.js
const fs = require("fs");
const DB = "D:/tarky/TEST/SPT/SPT_Data/database";
const quests = Object.values(JSON.parse(fs.readFileSync(`${DB}/templates/quests.json`)));
const locale = JSON.parse(fs.readFileSync(`${DB}/locales/global/en.json`));

// known EFT skill names (for text matching)
const SKILLS = [
  "Endurance","Strength","Vitality","Health","Stress Resistance","Metabolism","Immunity",
  "Perception","Intellect","Attention","Charisma","Memory","Throwing","Mag Drills",
  "Search","Surgery","Aim Drills","Troubleshooting","Light Vests","Heavy Vests",
  "Covert Movement","Sniper Rifles","Assault Rifles","Pistol","SMG","Shotgun","DMR",
  "Revolver","Melee","Recoil Control","Pump-Action","Bolt-Action","Throwing Weapons",
  "Sniping","Crafting","Hideout Management","Physical fitness","Field Medicine",
  "Vitality skill","Strength skill","Endurance skill",
];

const skillRe = new RegExp(
  "(skill level|reach (the )?(required )?(" + SKILLS.join("|") + ")\\b)", "i"
);

const found = [];
for (const q of quests) {
  const conds = (q.conditions && q.conditions.AvailableForFinish) || [];
  const texts = [];
  let hasSkillCond = false;
  for (const c of conds) {
    // explicit skill-level condition types used by EFT quests
    const ct = (c.conditionType || c._parent || "").toString();
    if (/^(Skill|Level)$/i.test(ct) && c.target && SKILLS.includes(c.target)) hasSkillCond = true;
    const t = locale[c.id];
    if (t) texts.push(t);
  }
  const joined = texts.join(" | ");
  if (hasSkillCond || skillRe.test(joined)) {
    found.push({ name: q.QuestName, trader: q.traderId, asks: joined });
  }
}

console.log(`=== SKILL / XP-GRIND QUESTS (${found.length}) ===`);
for (const f of found) console.log(`\n  ${f.name}\n    ${f.asks}`);
fs.writeFileSync(
  __dirname + "/output/skillQuests.json",
  JSON.stringify(found.map((f) => f.name), null, 2)
);
console.log("\nWrote generator/output/skillQuests.json");
