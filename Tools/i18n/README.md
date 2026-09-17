# Center's translations

The running job — what is done, what is next, in order:
**[TRANSLATION-INTO-MORE-LANGUAGES-PLAN.md](TRANSLATION-INTO-MORE-LANGUAGES-PLAN.md)**.

## Where the data lives

`strings.tsv` — one row per English string, one column per language. **This is the source.**

`loc_build.py` writes `ClawTweaksCenter/Core/Localization.Tables.cs` from it. Nothing else may
write that file; an edit made there survives exactly until the next run.

```
python Tools/i18n/loc_build.py            regenerate the C#
python Tools/i18n/loc_build.py --check    fail if the C# is not what the TSV says
python Tools/i18n/loc_lint.py             placeholder parity, stray whitespace, duplicates, width
python Tools/i18n/loc_lint.py --strict    exit 1 on an ERROR - run it before every regeneration
```

`loc_lint.py` is the only thing that catches a `{0}` that went missing in one language: `Loc.T`
and `Loc.F` never throw, so that mistake shows as a slightly wrong screen and nowhere else. Its
width report is the P2.4 worklist; a cell recorded in `left-in-english.tsv` is exempt.

An empty cell is a **decision**, not a gap: `Loc.T` returns the English when a key is absent, so
leaving a cell empty is how a translation that cannot fit its control is deliberately not shipped.

## The languages

| column | field | |
|---|---|---|
| `de` | German | shipped since the first round |
| `fr` | French | |
| `ko` | Korean | |
| `es` | Spanish | |
| `ru` | Russian | added in the thirteen-language round |
| `el` | Greek | |
| `zh-Hans` | ChineseSimplified | mainland |
| `zh-Hant` | ChineseTraditional | Taiwan / Hong Kong — MSI is a Taiwanese company |
| `it` | Italian | |
| `pt-BR` | Portuguese | Brazilian: ten times the players of pt-PT, and pt-PT reads it |
| `ja` | Japanese | |
| `pl` | Polish | |

## Finding what is missing

```
python Tools/i18n/loc_coverage.py                summary
python Tools/i18n/loc_coverage.py --gaps         every candidate, with its location
python Tools/i18n/loc_coverage.py --stale        keys no source file mentions any more
python Tools/i18n/loc_coverage.py --gaps-tsv F   the candidates as TSV rows
```

**Run this before translating, not after.** The first round was curated by hand for hours because
the survey then in use walked a list of 26 filenames that somebody had to remember to update.
Every screen added after it was written was invisible to it, which is why strings kept being found
on the device instead of in the report. This one walks every `.cs` file and decides by what the
literal looks like and where it sits.

Buckets, and what each one means for the work:

| bucket | what to do |
|---|---|
| `wrapped` | inside `Loc.T`/`Loc.F` with no table row. Renders English **today, in every language**. Add the row. |
| `builder` | reaches a builder that renders it. Needs `Loc.T` at the call site **and** a row. |
| `loose` | looks like text, no builder nearby. Read it and decide. |
| `interpolated` | built at runtime. No table can ever match it — the call site has to hand the **format** to `Loc.F`. A code change, not a row. |
| `log` | diagnostic text. Stays English on purpose: a log translated into a language the reader does not have is a log that cannot be reported. |

## The width rule

A translation must render no wider than `max(english + 5, english × 1.7)`, CJK counted double.
Center's chips, tabs and tiles are sized for the English word and do not grow.

**A translation that fails is shortened until it fits, not dropped.** Dropping was the old rule and
it was right for four languages that are close to English in length; with Russian, Polish, Greek and
Portuguese in the set it would leave those screens visibly half-English, which reads as broken
rather than as unfinished. What genuinely cannot be shortened honestly is left empty and recorded.

## Adding a language

1. A column in `strings.tsv`, and an entry in `LANGS` in `loc_build.py`.
2. `UiLanguage`, `Detect`, `NameOf`, `Order` and `TableFor` in `Core/Localization.cs`.
3. `python Tools/i18n/loc_build.py`.

`NameOf` returns the language's name **in that language**: somebody who has landed in a script they
cannot read has to be able to find their way out, and "Greek" does not help them — "Ελληνικά" does.

## The helper and the widget

The native OSD cards are translated too, and they follow **Center's** choice, not the OS: someone
who set Center to Italian on a German Windows means it. Center publishes the resolved language and
the helper falls back to the OS language, then to English.
