# Translating Center into more languages — the plan

Written to be picked up cold, by somebody who was not in the conversation that started it.
Tick the boxes as you go; **the boxes are the only record of progress that survives.**

---

## 0. What this job is

Center ships in five languages (English, German, French, Korean, Spanish). It is being taken to
**thirteen**: adding Russian, Greek, Chinese simplified, Chinese traditional, Italian, Brazilian
Portuguese, Japanese, Polish. The OSD notification cards of the helper and the widget get the same
treatment.

### Where the code is — this spans TWO repositories

| | path | what lives there |
|---|---|---|
| **Center** | `C:\Users\AlexB\Documents\Development\ClawTweaksCenter` | everything in P1 and P2 |
| **Helper + widget** | `C:\Users\AlexB\Documents\Development\ClawTweaks_GoTweaksFork` | everything in P3 |

Read `DEV_GUIDELINES.md` and `CONTRIBUTING.md` in the Center repo before touching anything. The
translation design is documented there, in the section beginning *"Translation happens at the
builders"* — this plan implements that design, it does not replace it.

**Building Center:** `dotnet publish -c Release` (needs the .NET 10 SDK). Only the exe under
`publish/` is shippable — see the Build section of DEV_GUIDELINES for why a plain `dotnet build`
output must not be handed out. The helper repo is the one built with `.\Build-Package.ps1`; that
script does **not** apply to Center.

### The decisions already taken (2026-09-14, by the repo owner)

These are settled. Do not relitigate them, and do not quietly do something else.

1. **Canonical table + generator.** `strings.tsv` is the source of truth;
   `Core/Localization.Tables.cs` is generated and never hand-edited.
2. **Over-wide translations are SHORTENED until they fit — not dropped.** This *reverses* the
   earlier rule. It was right for four languages close to English in length; with Russian, Polish,
   Greek and Portuguese in the set, dropping would leave those screens visibly half-English, which
   reads as broken rather than as unfinished. Only what cannot be shortened honestly stays English,
   and that goes in `left-in-english.tsv`.
3. **Interpolated strings go through `Loc.F`, with the format as the key** — not "translate the
   fixed part and concatenate the value", which bakes English word order in.
4. **Both Chinese scripts**, zh-Hans and zh-Hant. MSI is a Taiwanese company.
5. **OSD scope: the notification cards only.** The native Quick Panel's ~60 labels are out of scope.
6. **The helper follows CENTER's language, not the OS.** Somebody who set Center to Italian on a
   German Windows meant it. Fallback: Center's choice → OS language → English.

### The rules the translations have to pass

- **Width.** At most `max(english + 5, english × 1.7)`, CJK counted as two per character. Center's
  chips, tabs and tiles are sized for the English word and do not grow.
- **Menu headings stay English.** Home tiles keep their English titles; only their one-line
  descriptions are translated. `Library` is the deliberate exception and is translated everywhere.
- **An empty cell is a decision**, not an oversight — `Loc.T` returns the English for an absent key.
  Record the reason in the commit, and in `left-in-english.tsv` when it is a width decision.

---

## 1. Files, and which of them you may edit

```
Tools/i18n/
  strings.tsv                  SOURCE. English key + one column per language.
  left-in-english.tsv          SOURCE. What stays English on purpose, and why.
  loc_build.py                 generates the C#.  --check fails if it is stale.
  loc_coverage.py              finds strings the tables do not cover.
  loc_lint.py                  DOES NOT EXIST YET - task P1.6.
  glossary.tsv                 DOES NOT EXIST YET - task P2.1.
  README.md                    how the mechanism works.
  TRANSLATION-INTO-MORE-LANGUAGES-PLAN.md   this file.

ClawTweaksCenter/Core/
  Localization.cs              Loc.T / Loc.F, the enum, Detect, NameOf, TableFor. Hand-written.
  Localization.Tables.cs       GENERATED. Never edit. Never add a comment to the bottom of it -
                               the generator rewrites the whole file and it will not survive.
```

Eighteen one-off scripts from the German round were deleted in `3b93e71`. They are in the history.
**Do not resurrect `survey3.py`**: it walked a hand-maintained list of 26 filenames, so every screen
added after it was written was invisible to it, and its report *looked* complete. That is the single
reason the German round cost hours of manual curation on the device.

---

## P0 — Foundation ✅ done

- [x] TSV + generator; round trip proven identical for all four shipped languages
- [x] `loc_coverage.py` walks all 113 `.cs` files, classifying by shape and position
- [x] Thirteen languages wired: `UiLanguage`, `Detect`, `DetectChinese`, `NameOf`, `TableFor`,
      the settings picker
- [x] The eight new tables are empty, which renders correct English by design
- [x] `left-in-english.tsv` + generator footer — **this was a regression**: the first regeneration
      deleted the "left in English on purpose" block that DEV_GUIDELINES points readers at. It is
      data now, so it cannot be deleted by a regeneration again.

Commits `5b9c475`, `e8ae4b2`, `4846fc3`, `3b93e71`.

---

## P1 — Close the English side

None of this is translation. It is settling the key set **once**, because from here on a string
found late costs twelve translations instead of one.

Baseline measured 2026-09-15 (`python Tools/i18n/loc_coverage.py`):
`wrapped 4 · builder 63 · loose 494 · interpolated 111 · stale 48 · 718 keys`.

⚠️ **The correct fix is usually NOT to add `Loc.T` at the call site.** DEV_GUIDELINES is explicit:
*"Do not sprinkle `Loc.T` through new code. If a new string does not reach the screen through one of
those builders, that is the thing to fix."* So for each candidate, in this order of preference:
**(a)** it already reaches a builder → just add the TSV row; **(b)** it does not → route it through
the right builder; **(c)** only where neither is possible, `Loc.T` at the call site, and say why in
the commit.

- [ ] **P1.1 — the 4 `wrapped`.** They already call `Loc.T` and have no row, so they render English
      in *all four* shipped languages today:
      - `CenterMenuWindow.CenterSettings.cs:130` — `Experimental`
      - `CenterMenuWindow.CenterSettings.cs:139` — `Center starts the helper`
      - `CenterMenuWindow.CenterSettings.cs:147` — `Only in the full screen experience. …`
      - `CenterMenuWindow.Library.cs:2895` — `Tab visibility & order`
- [ ] **P1.2 — the 63 `builder`.** Tray menu, game menu (Favorite / Rename / Remove), Sleep /
      Hibernate / Launch / Edit, platform tabs, parts of Maintenance. Proper nouns and symbols
      (`Steam`, `Epic`, `Xbox`, `ROMs`, `A-Z`, `Z-A`) stay English — record that they were seen and
      rejected, or the next run of the survey reports them again as if nobody had looked.
- [ ] **P1.3 — the 494 `loose`**, in batches of ~60, **recording the verdict for every one**
      (see "Keeping progress" below). The bucket is deliberately generous; expect roughly 150–250
      real UI strings. The certificate instructions, controller diagnostics, download errors, device
      detection and parts of Maintenance are in here.
- [ ] **P1.4 — the 111 `interpolated` → `Loc.F`.** Decided by the owner, 2026-09-15, and
      DEV_GUIDELINES now says so. `$"Settings loaded for {gameName}"` becomes
      `Loc.F("Settings loaded for {0}", gameName)`; the **format** is the key, so a language may put
      the value somewhere other than where English puts it. Not "translate the fixed part and
      concatenate" — that bakes English word order in.
      Build after every file: `Loc.F` never throws, so a mistake here shows as a *wrong* screen
      rather than as an error, and only P1.6's placeholder check catches it.
- [ ] **P1.5 — the 48 stale keys** (`loc_coverage.py --stale`). Delete what is genuinely gone, keep
      what is built at runtime. A stale key costs twelve translations for nothing.

      ⚠️ **Three more went stale on 2026-09-15** and are deliberately still in the TSV, because
      removing them means regenerating `Localization.Tables.cs` and that belongs in this task rather
      than in an unrelated commit. The experimental settings band was removed from
      `CenterMenuWindow.CenterSettings.cs`, so these no longer reach any builder:

      - `Experimental`
      - `Center starts the helper`
      - `Only in the full screen experience. Measured here it changes nothing: Windows starts the
        helper about five seconds before Center is up.`

      They are harmless where they are — an unused key is never looked up — but they must not be
      translated. `loc_coverage.py --stale` will list them; this is only a note so nobody spends
      twelve translations on a row that has no screen.
- [ ] **P1.6 — write `loc_lint.py`**: placeholder parity (`{0}` in a translation iff in the English),
      stray leading/trailing whitespace, duplicate keys, and the width budget as a report. Needed
      **before** P2.
- [x] **P1.7 — DEV_GUIDELINES brought in line** (done 2026-09-15): `Loc.F` for interpolated strings,
      shorten-don't-drop, the left-in-English list generated from `left-in-english.tsv`, the tables
      generated from `strings.tsv`, `Loc.Order` named as dead, and the language list corrected to
      thirteen.

**Gate out of P1:** coverage reports `wrapped 0` and `builder 0`; `loc_build.py --check` passes;
`dotnet publish -c Release` succeeds.

---

## P2 — Translate

Expect 1000–1100 keys after P1, times twelve columns: roughly 13,000 cells. This is the bulk of the
job and it is several working sessions, not one.

- [ ] **P2.1 — `glossary.tsv` first**, ~40 recurring terms per language: TDP, Fan Curve, Profile,
      Handheld, Overlay, Helper, Center, Controller, DInput / XInput, Charge Limit, Sleep, Hibernate,
      Library, Onboarding, Backup, Restore. Fixed **before** any screen is translated, or the same
      word arrives three ways on three screens. Terms gamers use untranslated (TDP, FPS, DInput)
      stay English on purpose — translating them makes the screen harder to read, not easier.
- [ ] **P2.2 — fill the four existing columns' gaps** (de 717, fr 715, ko 718, es 717 of 718 today,
      plus everything P1 adds).
- [ ] **P2.3 — the eight new columns**, one language at a time, in batches of ~120 keys:
      - [ ] `it` Italian — closest to the existing set; a good first pass to shake out the process
      - [ ] `pt-BR` Portuguese
      - [ ] `pl` Polish
      - [ ] `ru` Russian
      - [ ] `el` Greek
      - [ ] `ja` Japanese
      - [ ] `zh-Hans` Chinese simplified
      - [ ] `zh-Hant` Chinese traditional — derived from Hans, then corrected. **Not** a character
            conversion: the vocabulary differs (软件 / 軟體, 视频 / 影片, 鼠标 / 滑鼠).
- [ ] **P2.4 — width pass per language.** `loc_lint.py` lists every over-budget cell; each is
      shortened until it fits. What cannot be shortened honestly goes in `left-in-english.tsv`
      with its width and budget, and the cell is left empty.

**Gate out of P2:** lint clean, `--check` passes, publishes, and each language opened once on the
device — Onboarding, Library, Settings, Maintenance.

---

## P3 — The helper and the widget OSD

**Different repository:** `C:\Users\AlexB\Documents\Development\ClawTweaks_GoTweaksFork`. Its own
rules apply there — build with `.\Build-Package.ps1` and nothing else, `Shared/Enums/Function.cs` is
append-only. The helper is .NET Framework 4.8, not .NET 10. Scope is the notification cards only.

- [ ] **P3.1 — how Center's language reaches the helper. ⚠️ Design decision, put it to the owner.**
      The pipe is the obvious carrier, but the helper shows cards before Center has said anything,
      so it needs somewhere to *read* the value at startup. Fallback chain: Center's choice → OS
      language → English.
- [ ] **P3.2 — a small `Loc` in the helper**, same shape and same "the key is the English string"
      rule, fed from the same `strings.tsv` so there is one source. The generator gets a second
      output.
- [ ] **P3.3 — the helper's card texts**, about ten:
      `Program.MSIClaw.cs` — charge-limit hint (~1954), hardware-mouse mode (~2754), controller
      connecting / connected / not connected (~4212, ~4228, ~4242);
      `Program.PowerActions.cs` — controller restoring / restored / not restored (~117, ~137, ~139).
      Cards split on the first newline: line 1 is the title, the rest is the body.
- [ ] **P3.4 — the widget's card texts.** `XboxGamingBar/GamingWidget.xaml.cs` (~3691) sends
      `title + "\n" + content`; profile applied / no profile / global restored / reverted. Plus
      `Features/QuickSettings/GamingWidget.QuickSettings.Actions.cs` (~3084),
      `SendActionNotificationAsync`. These are built by concatenation and need the same treatment as
      P1.4.
- [ ] **P3.5 — test on the device** with Center set to a language Windows does not have.

---

## P4 — Close out

- [ ] Attach a coverage report to the final commit, so the next reader sees what was left English.
- [ ] **Decide `Loc.Order` / `Loc.Next`.** Dead since the settings screen grew its own
      `LanguageOrder()`. Kept and marked; deleting them is the owner's call.
- [ ] Update the memory note `clawtweaks-i18n-thirteen-languages` and this file's boxes.

---

## Keeping progress — read this before starting P1.3

A batch-of-60 pass over 494 candidates is the one part of this job that **cannot** be reconstructed
from the repository afterwards. A string that was read and correctly rejected looks exactly like a
string nobody has read yet, so an interrupted pass silently starts over.

So: **P1.3 writes its verdict for every candidate into `Tools/i18n/triage.tsv`** — one row per
candidate, columns `file`, `line`, `english`, `verdict`, `note`, where verdict is one of
`ui` (row added) · `not-ui` · `proper-noun` · `log` · `dead` · `interpolated`. Commit it with each
batch. `loc_coverage.py` should then subtract what is already triaged, so the number it reports
falls as the work proceeds. Without that file the count stays at 494 forever and nobody can tell
what has been looked at.

Everything else survives on its own: the tables are in the TSV, the code is in git, the decisions
are at the top of this file, and the boxes above say where the work stopped.

---

## Known traps

- Some `.cs` files are **Windows-1252**, the rest UTF-8. The tools handle both. A key arriving with
  `\ufffd` in it can never match at runtime, so treat one appearing in a report as a tool bug, not a
  string to add. (A `?` in a *console* line is usually just the terminal's code page — check the
  file, not the terminal.)
- `Localization.Tables.cs` is **LF**, not CRLF, and starts with a BOM. The generator matches that;
  do not "fix" it.
- `UiLanguage` is persisted **by name**, not by ordinal (`CenterSettings.Language` round-trips
  `value.ToString()` through `Enum.TryParse`), so inserting a member in the middle is safe.
  **Renaming one is not:** an unparseable name falls back to `System`, so a user's pinned language
  quietly becomes "follow the OS". An earlier comment in `Localization.cs` claimed the opposite —
  if you find that wording anywhere else, it is wrong.
- `Loc.F` never throws: a mistyped placeholder falls back to the English format. That means a broken
  translation shows as a slightly wrong screen and **not** as an error — the lint in P1.6 is the only
  thing that catches it.
