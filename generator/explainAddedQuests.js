// Explain the quests that the merge ADDED to MainQuests.json.
//  - recovers the pre-merge list from git (HEAD~1) and diffs it against the
//    current MainQuests.json to get the exact set of newly-added quest names
//  - for each added quest: what immediately PRECEDES it in the trader chain
//    (its prerequisite once the chain is rebuilt) and what the quest ASKS
//    (authoritative locale task text from questDifficulty)
//
// Output: generator/output/addedQuests.json  (+ readable console summary)
// Run: node generator/explainAddedQuests.js
const fs = require("fs");
const path = require("path");
const { execSync } = require("child_process");

const ROOT = path.join(__dirname, "..");
const CFG = path.join(ROOT, "config");

const current = JSON.parse(fs.readFileSync(path.join(CFG, "MainQuests.json")));
const previous = JSON.parse(
  execSync("git show HEAD~1:config/MainQuests.json", { cwd: ROOT }).toString()
);
const flat = JSON.parse(
  fs.readFileSync(path.join(__dirname, "output", "questDifficulty.flat.json"))
);

// quest name -> difficulty info
const info = {};
flat.forEach((q) => (info[q.questName] = q));

// flatten a trader array (strings + embedded arrays) into an ordered name list,
// keeping track of which entry index each name belongs to
function flatten(arr) {
  const names = [];
  arr.forEach((entry) => {
    if (Array.isArray(entry)) entry.forEach((n) => names.push(n));
    else names.push(entry);
  });
  return names;
}

// set of all names that existed before the merge
const prevNames = new Set();
Object.values(previous).forEach((arr) => flatten(arr).forEach((n) => prevNames.add(n)));

const report = {};
let total = 0;

for (const [trader, arr] of Object.entries(current)) {
  const ordered = flatten(arr);
  const added = [];
  for (let i = 0; i < ordered.length; i++) {
    const name = ordered[i];
    if (prevNames.has(name)) continue; // not new
    const prev = i > 0 ? ordered[i - 1] : "(first quest for this trader)";
    const d = info[name] || {};
    added.push({
      quest: name,
      comesAfter: prev,
      asks: d.reqs || "(no task text found)",
      level: d.level ?? null,
      score: d.score ?? null,
      type: d.type ?? null,
      rewards: d.rewards ?? null,
    });
    total++;
  }
  if (added.length) report[trader] = added;
}

fs.writeFileSync(
  path.join(__dirname, "output", "addedQuests.json"),
  JSON.stringify(report, null, 2)
);

console.log(`=== ADDED QUESTS (${total} total) ===`);
for (const [trader, added] of Object.entries(report)) {
  console.log(`\n## ${trader} (+${added.length})`);
  for (const a of added) {
    console.log(`\n  ${a.quest}  [lvl ${a.level}, score ${a.score}]`);
    console.log(`    after: ${a.comesAfter}`);
    console.log(`    asks:  ${a.asks}`);
  }
}
console.log("\nWrote generator/output/addedQuests.json");
