# AQP — In-Game Testing TODO

Server-startup verification passes (logs show correct counts, server reaches "happy
playing"). These items can ONLY be confirmed by actually playing on a fresh profile.
Use the in-game quest-skipper to jump to specific quests where noted.

## High priority — could silently soft-lock a chain

- [ ] **Skill-only pass-through removal** — Quests whose ONLY objective was a Skill
  condition are now REMOVED entirely (zero-objective quests are dropped by the applier,
  and the chain rebuild closes the gap). Verify the affected chains still flow with the
  quest gone:
  - `Signal - Part 4` (Attention 8) — was mid the Mechanic Signal chain
    [Signal-1 → Signal-2 → Signal-3 → ~~Signal-4~~ → Import]. Confirm **Import** unlocks
    after Signal-3 and the chain isn't broken.
  - `The Survivalist Path - Combat Medic` (Vitality 5) — mid the Jaeger Survivalist chain.
    Confirm the chain continues past it.
  - (`Wet Job - Part 6`, `Health Care Privacy - Part 4` — verify whether these were
    skill-ONLY or had other objectives; if skill-only they're now removed.)
  - NOTE: `Psycho Sniper` and `Make an Impression` keep their kill objectives after the
    skill strip, so they are NOT removed — verify they're completable.

## Medium — feature correctness

- [ ] **4c — Weapon-build-strip (Test Drive 1–6)** — confirm each Test Drive quest is
  completable in a raid with ONLY the base weapon equipped (no specific scope/suppressor
  build), i.e. weaponModsInclusive/Exclusive are gone but the weapon requirement remains.

- [ ] **Container ladder** — confirm each awards the correct container in the reward window:
  - Debut → Alpha (2x2)
  - The Punisher - Part 1 → Beta (3x2)
  - The Punisher - Part 6 → Epsilon (4x2)
  - The Choice → Gamma (3x3) — AND confirm it correctly CONSUMES the Epsilon handover
  - Collector → Kappa (3x4)
  - Confirm the rouble money reward was removed from these (container is the prize).

- [ ] **Gunsmith → kill conversion** — confirm all 27 weapon-assembly quests (Gunsmith
  1–25, Old Friends Request, Breathing Room) show as "Eliminate N enemies using a
  <weapon>" with the correct weapon name + kill count, and are completable.

## Chain / progression sanity

- [ ] **Trader unlocks** — fresh profile: Prapor + Therapist available immediately;
  Skier locked until "Closer to the People"; Peacekeeper until "Search Mission";
  Mechanic until "Another Shipping Delay"; Ragman until "Debut"; Jaeger until "Airmail";
  Ref until "Tigr Safari".
- [ ] **Fence gate** — Fence's first quest locked until "The Punisher - Part 6" complete.
- [ ] **Linear gating** — quests unlock 2-at-a-time per trader (1-by-1 for Fence /
  Lighthousekeeper / BTR and all grouped sub-chains).
- [ ] **Reward scaling** — early-chain quests give small xp/money/standing, late-chain
  give large; money is in the right currency (Peacekeeper USD, Ref GP, rest RUB).
- [ ] **Labyrinth endgame** — with Lacy's enableLabyrinth + tweakExtracts on, confirm the
  relocated Labyrinth quests (This Tape Sucks, Indisputable Authority, Confidential Info,
  Vacate the Premises, Hypotheses Testing) are reachable and sit at chain ends.

## Cross-mod

- [ ] **Ref via Lacy's** — with LacyPvETweaks refChanges:true, confirm Ref/Arena quests
  are PvE-friendly and AQP left them untouched.
- [ ] **No double-tuning surprises** — note: findItemQuestModifier (0.5) currently halves
  find/handover counts ON TOP of the hand-tuned adjustReqsList values (deferred decision).
