/*
 * Quest Difficulty Generator  (data-gathering tool, NOT run on game start)
 * --------------------------------------------------------------------------
 * Reads the SPT_Data quest/trader/locale/item JSON directly and emits a
 * per-trader, difficulty-sorted JSON we can eyeball and tweak. The produced
 * data is used to build the eventual MainQuests quest-config by hand.
 *
 * Run:  node generator/generateQuestDifficulty.js
 * Out:  generator/output/questDifficulty.json   (trader -> quest -> details)
 *       generator/output/questDifficulty.flat.json (flat array, all traders)
 *
 * All scoring knobs live in WEIGHTS below — tune freely and re-run.
 */

const fs = require("fs");
const path = require("path");

// ---------------------------------------------------------------------------
// Paths (point at the TEST server's bundled data)
// ---------------------------------------------------------------------------
const DB = "D:/tarky/TEST/SPT/SPT_Data/database";
const QUEST_CONFIG = "D:/tarky/TEST/SPT/SPT_Data/configs/quest.json";
const OUT_DIR = path.join(__dirname, "output");

// ---------------------------------------------------------------------------
// SCORING WEIGHTS  — tweak these, then re-run. Higher = "harder".
// ---------------------------------------------------------------------------
const WEIGHTS = {
  // Start-gate: player level requirement is a strong difficulty proxy.
  perLevel: 1.0,

  // Kill objectives: (outer kill count) * perKill * targetWeight.
  perKill: 1.2,
  killTarget: {
    any: 1.0,
    savage: 1.0, // regular scavs
    anyPmc: 2.5,
    bear: 2.6,
    usec: 2.6,
    boss: 7.0, // bossXxx savageRole
    follower: 4.0, // followerXxx (goon squad etc.)
    cultist: 4.5, // sectantXxx
    marksman: 1.5, // sniper scavs
  },

  // Map-knowledge / situational sub-objectives inside a CounterCreator
  // (Location, VisitPlace, InZone, ExitName, ExitStatus, LaunchFlare, ...).
  perMapObjective: 1.5,

  // Hand over / find items.
  perHandover: 0.8, // per item, hand to trader
  perFind: 1.1, // per item, find in raid then hand
  firMultiplier: 1.8, // x if onlyFoundInRaid
  questItemMultiplier: 2.5, // x if it's a real "QuestItem" (map pickup)

  // Currency hand-overs ("give N roubles"): scored by rouble-normalized amount,
  // NOT by item count. amount-in-roubles / moneyDivisor = points.
  moneyDivisor: 100000, // 1,000,000 RUB -> 10 pts

  // Clamp any single objective count to this (some quests use sentinel values
  // like 9,999,999,999 for repeatable/"endless" objectives).
  maxObjectiveCount: 150,

  // Plant objectives (LeaveItemAtLocation / PlaceBeacon).
  perPlant: 4.0,
  plantTimeDivisor: 60, // + plantTime/divisor (seconds standing around)

  // Misc objective types.
  gunsmith: 5.0, // WeaponAssembly (flat)
  perSkillLevel: 1.2, // Skill condition (value = target level)
  traderLoyalty: 3.0, // reach loyalty level
  perSell: 0.5, // SellItemToTrader (per item)
  misc: 1.0, // anything unrecognised

  // Reward magnitude as a low-weight tie-breaker / sanity term.
  // Set to 0 to ignore rewards entirely in the score.
  rewardExpWeight: 0.0, // * (exp / 1000)
};

// ---------------------------------------------------------------------------
// EVENT FILTERING
//   Seasonal event quests (Christmas/Halloween/etc.) REQUIRE the event window,
//   so they are excluded. Non-seasonal event quests (season "None") do not
//   require a calendar event; we keep them but tag them so they can be reviewed.
// ---------------------------------------------------------------------------
const EVENTS = {
  excludeSeasonal: true, // drop quests with a real season (require the event)
  excludeNonSeasonal: false, // drop non-seasonal event quests too? (kept + tagged by default)
};

// ---------------------------------------------------------------------------
// "DON'T WANT" FLAGS  — pattern-based candidates for removal (a GUIDE, not a
//   hard rule; see the author's old removeList for the flavour). Flags are
//   attached to each quest's output for review; nothing is auto-removed unless
//   FLAGS.dropFlagged is set true.
// ---------------------------------------------------------------------------
const FLAGS = {
  dropFlagged: false, // if true, flagged quests are EXCLUDED like events
  extremeKillCount: 30, // single kill objective >= this -> "extreme-kills"
  bossGrindCount: 5, // killing >= this many of a boss/follower -> "boss-grind"
  multiObjectiveCount: 6, // >= this many finish conditions -> "multi-objective" (e.g. Collector)
  multiMapCount: 4, // same objective spread across >= this many maps -> "multi-map" (e.g. Escort)
};

// Enemy roles that only spawn during events / special modes (zombies, the
// Halloween cultist bosses, the arena Bloodhound). Quests requiring these can't
// be completed in a normal raid -> flagged "event-enemy".
const EVENT_ONLY_ROLES = new Set([
  "infectedassault",
  "infectedtagilla",
  "infectedcivil",
  "infectedlaborant",
  "infectedpmc",
  "sectantoni",
  "sectantprizrak",
  "sectantpredvestnik",
  "arenafighterevent",
]);

// The "Arena" trader (Lacy calls it "Ref") gives the Arena PvP-mode questline.
// Some of its quests have counter conditions tied to the Arena PvP game mode
// (winning matches) — these are IMPOSSIBLE in co-op PvE and MUST be rewritten by
// the RefModule (port of Lacy's EditRef). We detect those precisely via the
// Arena-mode counter condition types below and flag them "arena-pvp".
// (The trader's other quests are already normal raid objectives and need no rewrite.)
const ARENA_TRADER_ID = "6617beeaa9cfa777ca915b7c";
const ARENA_PVP_TYPES = new Set([
  "ArenaGameMode",
  "ArenaMatchPlace",
  "ArenaPlayerInTeamPlace",
]);




// ---------------------------------------------------------------------------
// Lookup tables
// ---------------------------------------------------------------------------
const CURRENCY_TPL = {
  "5449016a4bdc2d6f028b456f": "RUB",
  "569668774bdc2da2298b4568": "EUR",
  "5696686a4bdc2da3298b456a": "USD",
  "5d235b4d86f7742e017bc88a": "GP",
};

// Approx rouble value of one unit of each currency (for normalizing money goals).
const CURRENCY_TO_RUB = { RUB: 1, USD: 145, EUR: 160, GP: 22000 };

function formatMoney(n) {
  return n.toLocaleString("en-US");
}

// Friendly names for kill targets / boss roles (best-effort, extend as needed).
const SAVAGE_ROLE_LABEL = {
  bossbully: "Reshala",
  bossgluhar: "Glukhar",
  bosskilla: "Killa",
  bosskojaniy: "Shturman",
  bosssanitar: "Sanitar",
  bosstagilla: "Tagilla",
  bossknight: "Knight",
  followerbigpipe: "Big Pipe",
  followerbirdeye: "Bird Eye",
  bosszryachiy: "Zryachiy",
  bossboar: "Kaban",
  bosskolontay: "Kolontay",
  bosspartisan: "Partisan",
  bossboarsniper: "Kaban Sniper",
  sectantpriest: "Cultist Priest",
  sectantwarrior: "Cultist",
  pmcbot: "Raiders",
  exusec: "Rogues",
  arenafighterevent: "Bloodhound",
  bossKnight: "Knight",
};

// ---------------------------------------------------------------------------
// Load data
// ---------------------------------------------------------------------------
function loadJson(p) {
  return JSON.parse(fs.readFileSync(p, "utf8"));
}

const quests = loadJson(`${DB}/templates/quests.json`);
const items = loadJson(`${DB}/templates/items.json`);
const locale = loadJson(`${DB}/locales/global/en.json`);

// Event quests from the quest config -> { questId: { season, name, yearly } }
const questConfig = loadJson(QUEST_CONFIG);
const eventQuests = questConfig.eventQuests || {};
function eventInfo(questId) {
  const e = eventQuests[questId];
  if (!e) return null;
  const seasonal = e.season && String(e.season).toLowerCase() !== "none";
  return { season: e.season, seasonal, name: e.name };
}

// Trader id -> nickname
const traderNames = {};
for (const dir of fs.readdirSync(`${DB}/traders`)) {
  const baseFile = `${DB}/traders/${dir}/base.json`;
  if (!fs.existsSync(baseFile)) continue;
  try {
    const base = loadJson(baseFile);
    traderNames[dir] = base.nickname || dir;
  } catch {
    traderNames[dir] = dir;
  }
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
function itemName(tpl) {
  if (Array.isArray(tpl)) {
    const names = tpl.slice(0, 2).map(itemName);
    return tpl.length > 2 ? names.join("/") + "/…" : names.join("/");
  }
  return locale[`${tpl} ShortName`] || locale[`${tpl} Name`] || tpl;
}

function isQuestItem(tpl) {
  const tpls = Array.isArray(tpl) ? tpl : [tpl];
  return tpls.some((t) => items[t]?._props?.QuestItem === true);
}

function killTargetInfo(inner) {
  // inner = a "Kills" counter condition
  const roles = (inner.savageRole || []).map((r) => String(r).toLowerCase());
  if (roles.length) {
    const label =
      SAVAGE_ROLE_LABEL[roles[0]] ||
      roles[0].replace(/^boss|^follower|^sectant/, "");
    let weightKey = "follower";
    if (roles[0].startsWith("boss")) weightKey = "boss";
    else if (roles[0].startsWith("sectant")) weightKey = "cultist";
    else if (roles[0].includes("marksman")) weightKey = "marksman";
    return { label, weight: WEIGHTS.killTarget[weightKey] };
  }
  const target = String(inner.target || "Any");
  switch (target) {
    case "Savage":
      return { label: "Scavs", weight: WEIGHTS.killTarget.savage };
    case "AnyPmc":
      return { label: "PMCs", weight: WEIGHTS.killTarget.anyPmc };
    case "Bear":
      return { label: "BEAR", weight: WEIGHTS.killTarget.bear };
    case "Usec":
      return { label: "USEC", weight: WEIGHTS.killTarget.usec };
    case "Any":
      return { label: "enemies", weight: WEIGHTS.killTarget.any };
    default:
      return { label: target, weight: WEIGHTS.killTarget.any };
  }
}

// ---------------------------------------------------------------------------
// Score a single quest
// ---------------------------------------------------------------------------
function scoreQuest(quest) {
  let score = 0;
  const reqs = [];
  const flags = new Set();

  // Arena PvP-mode quests: only flag the ones whose counter conditions actually
  // require the Arena PvP game mode (win matches) — those are impossible in PvE
  // and must be rewritten by RefModule. The trader's other quests are fine as-is.
  if (quest.traderId === ARENA_TRADER_ID) {
    const needsArenaPvp = (quest.conditions?.AvailableForFinish || []).some((c) =>
      (c.counter?.conditions || []).some((ic) => ARENA_PVP_TYPES.has(ic.conditionType))
    );
    if (needsArenaPvp) flags.add("arena-pvp");
  }

  // --- start gate: level ---
  let level = 1;
  for (const c of quest.conditions?.AvailableForStart || []) {
    if (c.conditionType === "Level") level = Number(c.value) || 1;
  }
  score += level * WEIGHTS.perLevel;

  // --- objectives ---
  for (const c of quest.conditions?.AvailableForFinish || []) {
    const rawCount = Number(c.value) || 1;
    const count = Math.min(rawCount, WEIGHTS.maxObjectiveCount);
    const sentinel = rawCount > WEIGHTS.maxObjectiveCount;
    switch (c.conditionType) {
      case "CounterCreator": {
        const inner = c.counter?.conditions || [];
        const kills = inner.filter((i) => i.conditionType === "Kills");
        const mapObjs = inner.filter((i) =>
          [
            "Location",
            "VisitPlace",
            "InZone",
            "ExitName",
            "ExitStatus",
            "LaunchFlare",
            "ArenaGameMode",
            "ArenaMatchPlace",
            "ArenaPlayerInTeamPlace",
            "Equipment",
            "HealthEffect",
            "HealthBuff",
            "Shots",
            "Time",
            "UnderArtilleryFire",
          ].includes(i.conditionType)
        );
        if (kills.length) {
          // use the heaviest target among inner kills
          let best = { label: "enemies", weight: WEIGHTS.killTarget.any };
          for (const k of kills) {
            const info = killTargetInfo(k);
            if (info.weight > best.weight) best = info;
            // flag: requires event-only enemies (zombies / Halloween cultists / arena)
            for (const r of k.savageRole || []) {
              if (EVENT_ONLY_ROLES.has(String(r).toLowerCase())) {
                flags.add(`event-enemy:${r}`);
              }
            }
            // flag: grinding many bosses/followers
            const roleStr = (k.savageRole || []).map((r) => String(r).toLowerCase());
            const isBossish = roleStr.some((r) => r.startsWith("boss") || r.startsWith("follower") || r.startsWith("sectant"));
            if (isBossish && count >= FLAGS.bossGrindCount) flags.add("boss-grind");
          }
          score += count * WEIGHTS.perKill * best.weight;
          reqs.push(`Kill ${sentinel ? "∞" : count} ${best.label}`);
          // flag: extreme kill count (incl. sentinel "endless")
          if (sentinel || count >= FLAGS.extremeKillCount) flags.add("extreme-kills");
        } else {
          // exploration / placement style counter
          reqs.push(`${c.type || "Objective"} x${sentinel ? "∞" : count}`);
        }
        score += mapObjs.length * WEIGHTS.perMapObjective;
        if (mapObjs.some((m) => m.conditionType === "Location")) {
          const loc = mapObjs.find((m) => m.conditionType === "Location");
          const map = Array.isArray(loc.target) ? loc.target[0] : loc.target;
          if (map) reqs.push(`on ${map}`);
        }
        break;
      }
      case "HandoverItem": {
        const tpls = Array.isArray(c.target) ? c.target : [c.target];
        const ccy = tpls.map((t) => CURRENCY_TPL[t]).find(Boolean);
        if (ccy) {
          // money goal: score by rouble-normalized amount, not item count
          const rub = rawCount * (CURRENCY_TO_RUB[ccy] || 1);
          score += rub / WEIGHTS.moneyDivisor;
          reqs.push(`Hand over ${formatMoney(rawCount)} ${ccy}`);
          break;
        }
        const qItem = isQuestItem(c.target);
        let s = count * WEIGHTS.perHandover;
        if (c.onlyFoundInRaid) s *= WEIGHTS.firMultiplier;
        if (qItem) s *= WEIGHTS.questItemMultiplier;
        score += s;
        reqs.push(
          `Hand over ${count}x ${itemName(c.target)}${
            c.onlyFoundInRaid ? " (FiR)" : ""
          }`
        );
        break;
      }
      case "FindItem": {
        const qItem = isQuestItem(c.target);
        let s = count * WEIGHTS.perFind;
        if (c.onlyFoundInRaid) s *= WEIGHTS.firMultiplier;
        if (qItem) s *= WEIGHTS.questItemMultiplier;
        score += s;
        reqs.push(`Find ${count}x ${itemName(c.target)}`);
        break;
      }
      case "LeaveItemAtLocation":
      case "PlaceBeacon": {
        score += WEIGHTS.perPlant + (Number(c.plantTime) || 0) / WEIGHTS.plantTimeDivisor;
        reqs.push(c.conditionType === "PlaceBeacon" ? "Place beacon" : "Plant item");
        break;
      }
      case "WeaponAssembly": {
        score += WEIGHTS.gunsmith;
        reqs.push("Assemble weapon");
        break;
      }
      case "Skill": {
        score += count * WEIGHTS.perSkillLevel;
        reqs.push(`Raise ${c.target} to ${count}`);
        break;
      }
      case "TraderLoyalty": {
        score += WEIGHTS.traderLoyalty;
        reqs.push(`Reach LL${count} ${traderNames[c.target] || ""}`.trim());
        break;
      }
      case "SellItemToTrader": {
        score += count * WEIGHTS.perSell;
        reqs.push(`Sell ${count}x ${itemName(c.target)}`);
        break;
      }
      default: {
        score += WEIGHTS.misc;
        reqs.push(c.conditionType);
        break;
      }
    }
  }

  // --- reward magnitude (tie-break) ---
  const rewards = { exp: 0, standing: 0, money: 0, moneyCcy: "" };
  for (const r of quest.rewards?.Success || []) {
    if (r.type === "Experience") rewards.exp += Number(r.value) || 0;
    else if (r.type === "TraderStanding") rewards.standing += Number(r.value) || 0;
    else if (r.type === "Item") {
      const tpl = r.items?.[0]?._tpl;
      if (tpl && CURRENCY_TPL[tpl]) {
        rewards.money += Number(r.value) || 0;
        rewards.moneyCcy = CURRENCY_TPL[tpl];
      }
    }
  }
  score += (rewards.exp / 1000) * WEIGHTS.rewardExpWeight;

  const ev = eventInfo(quest._id);

  // --- extra "annoying" flags ---
  const finishConds = quest.conditions?.AvailableForFinish || [];
  // multi-objective bloat (e.g. Collector, Slaughterhouse): many finish conditions
  if (finishConds.length >= FLAGS.multiObjectiveCount) flags.add(`multi-objective:${finishConds.length}`);
  // one-session-only objectives (kill X in a single raid) — common "too annoying" tuning target
  if (finishConds.some((c) => c.oneSessionOnly)) flags.add("one-session-only");
  // same map repeated across many sub-objectives (e.g. Escort: kill on every map)
  const mapHits = {};
  for (const c of finishConds) {
    for (const ic of c.counter?.conditions || []) {
      if (ic.conditionType === "Location") {
        const m = Array.isArray(ic.target) ? ic.target[0] : ic.target;
        if (m) mapHits[m] = (mapHits[m] || 0) + 1;
      }
    }
  }
  const distinctMaps = Object.keys(mapHits).length;
  if (distinctMaps >= FLAGS.multiMapCount) flags.add(`multi-map:${distinctMaps}`);

  // --- human-readable objective text ---
  // The locale has an authoritative task string keyed by each finish-condition's id
  // (e.g. "Win a match in CheckPoint or LastHero mode in Arena"). Prefer that; fall
  // back to the synthesized summary (`reqs`) for any condition with no locale entry.
  const localeReqs = finishConds
    .map((c) => {
      const txt = locale[c.id];
      if (!txt) return null;
      // append a count when the objective wants more than 1 (locale text rarely says it)
      const n = Number(c.value) || 1;
      const clamped = Math.min(n, WEIGHTS.maxObjectiveCount);
      return n > 1 ? `${txt} (x${n > WEIGHTS.maxObjectiveCount ? "∞" : clamped})` : txt;
    })
    .filter(Boolean);

  return {
    score: Math.round(score * 100) / 100,
    level,
    type: quest.type,
    event: ev ? (ev.seasonal ? ev.season : "non-seasonal") : null,
    flags: [...flags],
    reqs: localeReqs.length ? localeReqs.join("; ") : reqs.join("; ") || "(no objectives)",
    reqsRaw: reqs.join("; ") || "(no objectives)",
    rewards: {
      exp: rewards.exp,
      standing: Math.round(rewards.standing * 100) / 100,
      money: rewards.money,
      moneyCcy: rewards.moneyCcy,
    },
    id: quest._id,
  };
}

// ---------------------------------------------------------------------------
// Build per-trader, score-sorted output
// ---------------------------------------------------------------------------
const byTrader = {}; // traderName -> [ {questName, ...scored} ]
const excluded = []; // [{questName, trader, reason}]
for (const quest of Object.values(quests)) {
  const ev = eventInfo(quest._id);
  if (ev) {
    if (ev.seasonal && EVENTS.excludeSeasonal) {
      excluded.push({ questName: quest.QuestName, trader: traderNames[quest.traderId] || quest.traderId, reason: `seasonal:${ev.season}` });
      continue;
    }
    if (!ev.seasonal && EVENTS.excludeNonSeasonal) {
      excluded.push({ questName: quest.QuestName, trader: traderNames[quest.traderId] || quest.traderId, reason: "non-seasonal-event" });
      continue;
    }
  }
  const trader = traderNames[quest.traderId] || quest.traderId;
  const scored = scoreQuest(quest);
  if (FLAGS.dropFlagged && scored.flags.length) {
    excluded.push({ questName: quest.QuestName, trader, reason: `flags:${scored.flags.join("|")}` });
    continue;
  }
  if (!byTrader[trader]) byTrader[trader] = [];
  byTrader[trader].push({ questName: quest.QuestName, ...scored });
}

const output = {}; // traderName -> { questName -> details }
const flat = []; // [{trader, order, questName, ...}]
for (const trader of Object.keys(byTrader).sort()) {
  const sorted = byTrader[trader].sort(
    (a, b) =>
      a.score - b.score || a.level - b.level || a.questName.localeCompare(b.questName)
  );
  output[trader] = {};
  sorted.forEach((q, i) => {
    const { questName, ...rest } = q;
    output[trader][questName] = { order: i + 1, ...rest };
    flat.push({ trader, order: i + 1, questName, ...rest });
  });
}

// ---------------------------------------------------------------------------
// Write
// ---------------------------------------------------------------------------
if (!fs.existsSync(OUT_DIR)) fs.mkdirSync(OUT_DIR, { recursive: true });
fs.writeFileSync(
  path.join(OUT_DIR, "questDifficulty.json"),
  JSON.stringify(output, null, 2)
);
fs.writeFileSync(
  path.join(OUT_DIR, "questDifficulty.flat.json"),
  JSON.stringify(flat, null, 2)
);
fs.writeFileSync(
  path.join(OUT_DIR, "excludedEventQuests.json"),
  JSON.stringify(excluded, null, 2)
);
// Flagged quests kept in output (annoying-but-keepable: trim/tune candidates)
const flaggedKept = flat.filter((q) => (q.flags || []).length);
fs.writeFileSync(
  path.join(OUT_DIR, "flaggedQuests.json"),
  JSON.stringify(
    flaggedKept.map((q) => ({ trader: q.trader, questName: q.questName, flags: q.flags, reqs: q.reqs })),
    null,
    2
  )
);

// ---------------------------------------------------------------------------
// Console summary
// ---------------------------------------------------------------------------
console.log("Weights used:", JSON.stringify(WEIGHTS));
console.log("Event filter:", JSON.stringify(EVENTS));
console.log(`\nTotal quests scored: ${flat.length}  (excluded events: ${excluded.length})\n`);
for (const trader of Object.keys(output)) {
  const entries = Object.entries(output[trader]);
  const min = entries[0];
  const max = entries[entries.length - 1];
  console.log(
    `${trader.padEnd(16)} ${String(entries.length).padStart(3)} quests   ` +
      `easiest: ${min[0]} (${min[1].score})  hardest: ${max[0]} (${max[1].score})`
  );
}
if (excluded.length) {
  console.log(`\nExcluded event quests (${excluded.length}):`);
  excluded.forEach((e) => console.log(`  - ${e.questName.padEnd(34)} [${e.reason}] (${e.trader})`));
}
const nonSeasonalKept = flat.filter((q) => q.event === "non-seasonal");
if (nonSeasonalKept.length) {
  console.log(`\nNon-seasonal event quests KEPT + tagged (${nonSeasonalKept.length}) — review:`);
  nonSeasonalKept.forEach((q) => console.log(`  - ${q.questName.padEnd(34)} (${q.trader})`));
}
if (flaggedKept.length) {
  // group by flag category for a quick scan
  const byFlag = {};
  flaggedKept.forEach((q) =>
    q.flags.forEach((f) => {
      const cat = f.split(":")[0];
      (byFlag[cat] = byFlag[cat] || []).push(q.questName);
    })
  );
  console.log(`\nFlagged "annoying" quests KEPT (${flaggedKept.length}) — trim/tune candidates:`);
  Object.entries(byFlag)
    .sort((a, b) => b[1].length - a[1].length)
    .forEach(([cat, names]) =>
      console.log(`  ${cat.padEnd(18)} ${names.length}  e.g. ${[...new Set(names)].slice(0, 5).join(", ")}`)
    );
}
console.log(`\nWrote:\n  ${path.join(OUT_DIR, "questDifficulty.json")}\n  ${path.join(OUT_DIR, "questDifficulty.flat.json")}\n  ${path.join(OUT_DIR, "excludedEventQuests.json")}\n  ${path.join(OUT_DIR, "flaggedQuests.json")}`);
