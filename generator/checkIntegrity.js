// Structural integrity check for the curated linear progression.
//
// Flags any curated quest that consumes a QUEST ITEM (QuestItem=true, cannot be
// bought/looted freely) whose only in-game source is:
//   (a) a quest that is NOT in the curated set (removed)  -> HARD BREAK
//   (b) a curated quest in a DIFFERENT trader             -> CROSS-TRADER BREAK
//   (c) a curated quest LATER in the SAME trader order    -> ORDER BREAK
// Self-provided (same quest finds+hands the item) = fine.
//
// Run: node generator/checkIntegrity.js
const fs = require("fs");
const path = require("path");
const DB = "D:/tarky/TEST/SPT/SPT_Data/database";
const CFG = path.join(__dirname, "..", "config");

const questsArr = Object.values(JSON.parse(fs.readFileSync(`${DB}/templates/quests.json`)));
const items = JSON.parse(fs.readFileSync(`${DB}/templates/items.json`));
const locale = JSON.parse(fs.readFileSync(`${DB}/locales/global/en.json`));
const mainQuests = JSON.parse(fs.readFileSync(path.join(CFG, "MainQuests.json")));

const CONSUME = new Set(["HandoverItem", "LeaveItemAtLocation", "PlaceBeacon", "WeaponAssembly"]);
// Verified self-contained despite the heuristic: the quest item spawns on the map for
// THIS standalone quest (it isn't carried from another quest). Reviewed 2026-06.
const VERIFIED_OK = new Set([
  "Shipping Delay - Part 1", // own "Package" spawns on Woods; "A Helping Hand" just reuses the tpl
]);
const itemName = (tpl) => locale[`${tpl} ShortName`] || locale[`${tpl} Name`] || tpl;
const isQuestItem = (tpl) => !!(items[tpl] && items[tpl]._props && items[tpl]._props.QuestItem === true);
const targets = (t) => (t == null ? [] : Array.isArray(t) ? t.flat(Infinity) : [t]);

const byName = {};
const findsIndex = {};   // tpl -> Set(questName) FindItem
const startGrant = {};   // tpl -> Set(questName) grants on Start (so you HAVE it)
for (const q of questsArr) {
  byName[q.QuestName] = q;
  for (const c of (q.conditions && q.conditions.AvailableForFinish) || [])
    if (c.conditionType === "FindItem") for (const tpl of targets(c.target)) (findsIndex[tpl] ||= new Set()).add(q.QuestName);
  const r = q.rewards || {};
  for (const k of ["Started", "Success"])
    for (const rr of r[k] || []) if (rr.items) for (const it of rr.items) if (it._tpl) (startGrant[it._tpl] ||= new Set()).add(q.QuestName);
}

// curated order: questName -> {trader, index}
const flatten = (arr) => arr.flatMap((e) => (Array.isArray(e) ? e : [e]));
const orderOf = {};
for (const [trader, arr] of Object.entries(mainQuests)) flatten(arr).forEach((n, i) => (orderOf[n] = { trader, index: i }));
const curated = new Set(Object.keys(orderOf));

const breaks = [];
for (const name of curated) {
  const q = byName[name];
  if (!q) continue;
  if (VERIFIED_OK.has(name)) continue;
  const me = orderOf[name];
  const finish = (q.conditions && q.conditions.AvailableForFinish) || [];
  const selfFinds = new Set();
  for (const c of finish) if (c.conditionType === "FindItem") for (const tpl of targets(c.target)) selfFinds.add(tpl);
  // items this quest grants itself on start
  const selfGrant = new Set();
  for (const k of ["Started", "Success"]) for (const rr of (q.rewards && q.rewards[k]) || []) if (rr.items) for (const it of rr.items) if (it._tpl) selfGrant.add(it._tpl);

  for (const c of finish) {
    if (!CONSUME.has(c.conditionType)) continue;
    const tgts = targets(c.target);
    // "any-of" handover (e.g. "hand over 3 of these 58 meds") -> not a hard single-item req
    if (tgts.length > c.value) continue;
    // a quest that grants ANY item on start is self-supplying its plant/handover item
    // (BSG often reuses a legacy quest-item tpl on the condition vs the granted tpl)
    const grantsSomething = selfGrant.size > 0 && (c.conditionType === "LeaveItemAtLocation" || c.conditionType === "PlaceBeacon");
    for (const tpl of tgts) {
      if (!isQuestItem(tpl)) continue;          // only hard quest items matter
      if (selfFinds.has(tpl) || selfGrant.has(tpl) || grantsSomething) continue; // self-contained
      // providers
      const finders = [...(findsIndex[tpl] || [])].filter((n) => n !== name);
      const granters = [...(startGrant[tpl] || [])].filter((n) => n !== name);
      const providers = [...new Set([...finders, ...granters])];
      // classify
      const curatedProviders = providers.filter((p) => curated.has(p));
      let kind = null, detail = "";
      if (providers.length === 0) {
        kind = "NO SOURCE"; detail = "no quest finds/grants this item at all";
      } else if (curatedProviders.length === 0) {
        kind = "HARD BREAK (provider removed)"; detail = providers.map((p) => p).join(", ");
      } else {
        // pick best curated provider
        const before = curatedProviders.filter((p) => orderOf[p].trader === me.trader && orderOf[p].index < me.index);
        const sameTraderAny = curatedProviders.filter((p) => orderOf[p].trader === me.trader);
        const crossTrader = curatedProviders.filter((p) => orderOf[p].trader !== me.trader);
        if (before.length) continue; // fine: a same-trader earlier quest provides it
        if (sameTraderAny.length) { kind = "ORDER BREAK (provider is LATER same trader)"; detail = sameTraderAny.map((p) => `${p} @${orderOf[p].index}`).join(", "); }
        else { kind = "CROSS-TRADER"; detail = crossTrader.map((p) => `[${orderOf[p].trader}] ${p}`).join(", "); }
      }
      breaks.push({ trader: me.trader, idx: me.index, quest: name, cond: c.conditionType, item: itemName(tpl), tpl, kind, detail });
    }
  }
}

const order = ["NO SOURCE", "HARD BREAK (provider removed)", "CROSS-TRADER", "ORDER BREAK (provider is LATER same trader)"];
breaks.sort((a, b) => order.indexOf(a.kind) - order.indexOf(b.kind));
if (!breaks.length) console.log("CLEAN: no quest-item dependency breaks in the curated set.");
for (const b of breaks) {
  console.log(`[${b.kind}]  [${b.trader} #${b.idx}] ${b.quest}`);
  console.log(`     ${b.cond} "${b.item}"  <- ${b.detail}`);
}
