# Thirteen languages — the plan

Status of the whole job, kept here so it survives a lost conversation. Tick the boxes.

The order below is not a preference. Translating before the English key set is settled means every
string found afterwards costs **twelve** translations instead of one, and the first round already
proved that strings do get found afterwards.

---

## P0 — Foundation ✅ done

- [x] `strings.tsv` is the source; `loc_build.py` writes `Core/Localization.Tables.cs`
- [x] `loc_coverage.py` reads all 113 `.cs` files (the old survey walked 26 filenames by hand)
- [x] Round trip proven: all four shipped languages come back byte-identical as data
- [x] Thirteen languages wired: enum, `Detect`, `DetectChinese`, `NameOf`, `TableFor`, picker
- [x] Builds clean; the eight empty tables render English by design

Commits `5b9c475`, `e8ae4b2`, `4846fc3`.

---

## P1 — Close the English side

Nothing here is translation. It is making sure the key set is complete **once**.

Measured at the start of P1: 4 wrapped, 63 builder, 494 loose, 111 interpolated, 48 stale.

- [ ] **P1.1 — the 4 `wrapped`** (30 min). Already call `Loc.T`, so they render English in *all four*
      shipped languages today. Only need rows in the TSV.
- [ ] **P1.2 — the 63 `builder`** (half a day). Each reaches a builder that renders it. Wrap the call
      site in `Loc.T`, add the key. Includes the tray menu, the game menu (Favorite / Rename /
      Remove), Sleep / Hibernate / Launch / Edit, the platform tabs, parts of Maintenance.
      Judgement call per item: platform names (`Steam`, `Epic`, `Xbox`, `ROMs`) and `A-Z` stay
      English — they are proper nouns and symbols, not sentences.
- [ ] **P1.3 — the 494 `loose`** (1–2 days, in batches of ~60). The bucket is deliberately generous:
      most entries will turn out to be tooltips, exception text or dead strings. Each batch ends
      with a build. Expect roughly 150–250 real UI strings out of it — the certificate instructions,
      controller diagnostics, download errors, device detection and Maintenance are all in here.
- [ ] **P1.4 — the 111 `interpolated`** (half a day). `$"Settings loaded for {gameName}"` can never
      match a table. Each needs the **format** handed to `Loc.F` at the call site. This is a code
      change and it is the one place where a mistake shows up as a wrong screen rather than an
      English one, so: build after every file.
- [ ] **P1.5 — the 48 stale keys**. Keys no source file mentions. Delete the ones that are genuinely
      gone, keep the ones that are built at runtime. Cheap, but do it before translating — a stale
      key costs twelve translations for nothing.
- [ ] **P1.6 — write `loc_lint.py`**: placeholder parity (`{0}` present in translation iff present
      in English), stray leading/trailing whitespace, duplicate keys, and the width rule as a
      report. Needed before P2, not after.

**Gate out of P1:** `loc_coverage.py` reports `wrapped 0` and `builder 0`; `loc_build.py --check`
passes; `.\Build-Package.ps1` succeeds.

---

## P2 — Translate

Key count after P1 will be roughly 1000–1100. Twelve columns. That is ~13,000 cells, and it is the
bulk of the whole job — several working sessions, not one.

- [ ] **P2.1 — the glossary first**, one table of ~40 recurring terms per language: TDP, Fan Curve,
      Profile, Handheld, Overlay, Helper, Center, Controller, DInput / XInput, Charge Limit, Sleep,
      Hibernate, Library, Onboarding. Fixed **before** any screen is translated, or the same word
      arrives three ways on three screens. Terms that gamers use untranslated (TDP, FPS, DInput)
      stay English on purpose — translating them makes the screen *harder* to read, not easier.
- [ ] **P2.2 — fill the four existing columns' gaps** (de, fr, ko, es). New keys from P1 only.
- [ ] **P2.3 — the eight new columns**, one language at a time, in batches of ~120 keys:
      - [ ] `it` Italian — closest to the existing set, good first pass to shake out the process
      - [ ] `pt-BR` Portuguese
      - [ ] `es`-adjacent done → `pl` Polish
      - [ ] `ru` Russian
      - [ ] `el` Greek
      - [ ] `ja` Japanese
      - [ ] `zh-Hans` Chinese simplified
      - [ ] `zh-Hant` Chinese traditional — derived from Hans, then corrected for Taiwan usage.
            **Not** a character conversion: the vocabulary differs (软件 / 軟體, 视频 / 影片).
- [ ] **P2.4 — width pass per language.** `loc_lint.py` reports every cell over
      `max(en+5, en×1.7)`, CJK counted double. Each one gets **shortened until it fits** — the
      decision from this round, replacing the old "drop it" rule. What cannot be shortened honestly
      is left empty and listed in the commit message.

**Gate out of P2:** lint clean, `--check` passes, builds, and each language opened once on the
device — Onboarding, Library, Settings, Maintenance.

---

## P3 — The helper and the widget OSD

Scope is the **notification cards only**. The native Quick Panel's ~60 labels are out of scope by
decision.

- [ ] **P3.1 — Center publishes its resolved language.** The helper must follow *Center's* choice,
      not the OS: someone who set Center to Italian on a German Windows meant it. Mechanism to
      decide — the existing pipe is the obvious carrier, but the helper also needs the value at
      startup, before Center has said anything, so it needs somewhere to read it. Fallback chain:
      Center's choice → OS language → English.
- [ ] **P3.2 — a small `Loc` in the helper** (.NET Framework 4.8). Same shape as Center's, same
      "key is the English string" rule, fed by the same TSV so there is one source. Generator gets a
      second output.
- [ ] **P3.3 — wrap the helper's card texts.** Known set, about ten: controller connecting /
      connected / not connected, controller restoring / restored / not restored, hardware-mouse
      mode, charge-limit hint.
- [ ] **P3.4 — the widget's card texts.** Profile applied / no profile / global restored / reverted,
      and the per-action notifications. These are built by string concatenation today and need the
      `Loc.F` treatment, same as P1.4.
- [ ] **P3.5 — test on the device** with Center set to a language the OS does not have.

---

## P4 — Close out

- [ ] Coverage report attached to the final commit, so the next person sees what was left English
      and why.
- [ ] Decide `Loc.Order` / `Loc.Next`: dead since the settings screen grew its own `LanguageOrder()`.
      Kept, marked, owner's call.

---

## Rules that apply throughout

- `Localization.Tables.cs` is **generated**. Never hand-edited.
- Build only with `.\Build-Package.ps1`.
- An empty cell is a decision, not an oversight. Record why in the commit.
- Some `.cs` files are Windows-1252. The tools handle it; a key that arrives with `�` in it can
  never match at runtime, so treat one appearing in a report as a tool bug, not a string to add.
