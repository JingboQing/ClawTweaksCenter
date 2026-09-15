# -*- coding: utf-8 -*-
"""Lints strings.tsv - the checks that nothing else runs.

    python Tools/i18n/loc_lint.py                report everything
    python Tools/i18n/loc_lint.py --strict       exit 1 on any ERROR (for a pre-commit or CI step)
    python Tools/i18n/loc_lint.py --lang ru      only one column
    python Tools/i18n/loc_lint.py --width-only   just the width report, sorted worst first
    python Tools/i18n/loc_lint.py --inno         lint inno.tsv instead - the SETUP's strings

WHY IT EXISTS. Loc.F never throws: a translation whose {0} went missing falls back to the English
format, and a translation with a {1} the English does not have renders the literal "{1}". Both show
as a slightly wrong screen, never as an error, and nothing before this script looked for them.
The same goes for the width rule - it was applied by hand in the German round and then drifted.

WHAT IS AN ERROR AND WHAT IS A WARNING:
  ERROR    placeholder set differs from the English  -> Loc.F renders the wrong thing
  ERROR    duplicate English key                     -> the later row silently wins
  ERROR    a row with more cells than the header     -> a stray tab shifted every language right
  ERROR    leading/trailing whitespace in a cell     -> invisible on screen, breaks the lookup
  WARN     translation wider than the budget         -> may clip; shorten (decision 2) or record
                                                        it in left-in-english.tsv and empty the cell
  WARN     translation identical to the English      -> usually a copy-paste, sometimes a brand
  WARN     double space, or a space before . , : ; ! ?

THE WIDTH RULE, same as loc_build.py states it: max(english + 5, english * 1.7), CJK counted double.
A cell listed in left-in-english.tsv for that language is exempt - that file IS the record of the
decision, so the lint does not nag about what was decided.

PLACEHOLDERS. {0} {1} ... as C# composite format uses them. A doubled brace {{ }} is a literal
brace and is not a placeholder.

--inno IS THE SECOND GRAMMAR, NOT A RELAXED ONE. The setup's strings live in inno.tsv and use
Inno's own notation: %1 and %2 are FmtMessage slots, %n is a line break, %% is a literal percent.
Slot parity is an ERROR for the same reason it is in C#: MF() renders the argument into whatever
slots the chosen language has, so a dropped %1 silently loses the tool name and a %2 the language
does not have renders as the literal text. %n parity is a WARNING - a lost line break rearranges
a wizard page without breaking it.

WIDTH IS NOT CHECKED FOR THE SETUP, and that is deliberate. Inno wizard pages wrap, but fixed
Width controls do not grow, and this script cannot tell which key ends up on a button. The one
button that already measures itself is the Windows-Settings one (CalculateButtonWidth in the
.iss). Every other fixed width is proven by running the setup with /LANG=xx, which is P3b.4.
"""
import io, os, re, sys, collections, unicodedata

HERE = os.path.dirname(os.path.abspath(__file__))
TSV = os.path.join(HERE, 'strings.tsv')
LEFT = os.path.join(HERE, 'left-in-english.tsv')

PLACEHOLDER = re.compile(r'(?<!\{)\{(\d+)(?:[:,][^{}]*)?\}(?!\})')
SPACE_BEFORE_PUNCT = re.compile(r' [.,:;!?](?:\s|$)')


def visual_width(s):
    """Characters, with East Asian wide/full-width ones counted double - they render at roughly
    twice the width of a Latin letter in the fonts Center uses."""
    w = 0
    for ch in s:
        if unicodedata.combining(ch):
            continue
        w += 2 if unicodedata.east_asian_width(ch) in ('W', 'F') else 1
    return w


def budget(english):
    e = visual_width(english)
    return max(e + 5, int(e * 1.7))


def placeholders(s):
    return collections.Counter(PLACEHOLDER.findall(s))


def load_left():
    exempt = set()
    if not os.path.exists(LEFT):
        return exempt
    for i, line in enumerate(io.open(LEFT, encoding='utf-8')):
        if i == 0 or not line.strip():
            continue
        p = line.rstrip('\n').split('\t')
        if len(p) >= 2:
            exempt.add((p[0], p[1]))
    return exempt


# ---------------------------------------------------------------------------------------------
# --inno: the setup's strings, in Inno's grammar
# ---------------------------------------------------------------------------------------------

INNO_TSV = os.path.join(HERE, 'inno.tsv')

# %1 .. %9 are FmtMessage slots. %n is a newline and %% a literal percent, so neither is a slot:
# the negative lookbehind keeps "%%1" from being read as one.
INNO_SLOT = re.compile(r'(?<!%)%([1-9])')
INNO_BREAK = re.compile(r'(?<!%)%n')


def inno_slots(s):
    return collections.Counter(INNO_SLOT.findall(s))


def lint_inno(only_lang=None):
    rows = [l.rstrip(chr(10)).rstrip(chr(13)).split(chr(9))
            for l in io.open(INNO_TSV, encoding='utf-8')]
    rows = [r for r in rows if r and r[0].strip()]
    head = rows[0]
    langs = head[2:]
    if only_lang:
        langs = [c for c in langs if c == only_lang]

    errors = warnings = 0
    seen = {}
    for n, row in enumerate(rows[1:], start=2):
        row += [''] * (len(head) - len(row))
        if len(row) > len(head):
            print('ERROR  %d: %d cells against a header of %d - a stray tab shifted the row'
                  % (n, len(row), len(head)))
            errors += 1
            continue
        key, english = row[1], row[2]
        if key in seen:
            print('ERROR  %d: duplicate key %s (first seen on line %d)' % (n, key, seen[key]))
            errors += 1
        seen[key] = n

        for col, cell in zip(head[2:], row[2:]):
            if col not in langs or not cell:
                continue
            # Edge whitespace is not automatically wrong here: PayloadAnd is ' and', a joiner
            # concatenated onto a list, and stripping it would run two words together. What
            # would be wrong is a translation whose edges differ from the English's.
            if (cell != cell.strip()) != (english != english.strip()):
                print('ERROR  %d %s/%s: edge whitespace %r against English %r'
                      % (n, col, key, cell[:1] + '...' + cell[-1:],
                         english[:1] + '...' + english[-1:]))
                errors += 1
            if col == 'en':
                continue
            if inno_slots(cell) != inno_slots(english):
                print('ERROR  %d %s/%s: slots %s against English %s'
                      % (n, col, key, sorted(inno_slots(cell).elements()),
                         sorted(inno_slots(english).elements())))
                errors += 1
            if len(INNO_BREAK.findall(cell)) != len(INNO_BREAK.findall(english)):
                print('WARN   %d %s/%s: %d line breaks against English %d'
                      % (n, col, key, len(INNO_BREAK.findall(cell)),
                         len(INNO_BREAK.findall(english))))
                warnings += 1
            if cell == english and len(english) > 3:
                print('WARN   %d %s/%s: identical to the English' % (n, col, key))
                warnings += 1
            double = '  ' in cell and '  ' not in english
            spaced = SPACE_BEFORE_PUNCT.search(cell) and not SPACE_BEFORE_PUNCT.search(english)
            # French typography puts a space before : ; ! ? on purpose - the C# half knows this
            # too, and the two halves must not disagree about the same language.
            if col == 'fr' and spaced and not double:
                spaced = False
            if double or spaced:
                print('WARN   %d %s/%s: double space or space before punctuation' % (n, col, key))
                warnings += 1

    filled = {c: 0 for c in head[2:]}
    for row in rows[1:]:
        row += [''] * (len(head) - len(row))
        for col, cell in zip(head[2:], row[2:]):
            if cell.strip():
                filled[col] += 1
    print(chr(10) + 'inno.tsv: %d keys; %s'
          % (len(rows) - 1, ', '.join('%s %d' % (c, filled[c]) for c in head[2:])))
    print('%d error(s), %d warning(s). Width is not checked here - see the note at the top.'
          % (errors, warnings))
    return errors


def main():
    argv = sys.argv[1:]
    strict = '--strict' in argv
    width_only = '--width-only' in argv
    only = argv[argv.index('--lang') + 1] if '--lang' in argv else None

    if '--inno' in argv:
        errors = lint_inno(only)
        return 1 if (strict and errors) else 0

    lines = io.open(TSV, encoding='utf-8').read().split('\n')
    header = lines[0].split('\t')
    langs = header[1:]
    if only and only not in langs:
        sys.stderr.write('no such column: %s (have %s)\n' % (only, ', '.join(langs)))
        return 2

    exempt = load_left()
    errors, warns, widths = [], [], []
    seen = {}

    for n, line in enumerate(lines[1:], start=2):
        if not line.strip():
            continue
        cells = line.split('\t')
        if len(cells) > len(header):
            errors.append('%d: %d cells but the header has %d - a stray tab' % (n, len(cells), len(header)))
            continue
        while len(cells) < len(header):
            cells.append('')
        en = cells[0]

        if en in seen:
            errors.append('%d: duplicate key (first at %d): %s' % (n, seen[en], en[:80]))
        seen[en] = n
        if en != en.strip():
            errors.append('%d: English key has leading/trailing whitespace: %r' % (n, en))

        en_ph = placeholders(en)
        en_w = visual_width(en)
        bud = budget(en)

        for lang, cell in zip(langs, cells[1:]):
            if only and lang != only:
                continue
            if cell == '':
                continue
            if cell != cell.strip():
                errors.append('%d %s: leading/trailing whitespace: %r' % (n, lang, cell))
            ph = placeholders(cell)
            if ph != en_ph:
                errors.append('%d %s: placeholders %s, English has %s: %s'
                              % (n, lang, dict(ph) or '{}', dict(en_ph) or '{}', en[:60]))
            if cell == en and len(en) > 3:
                warns.append('%d %s: identical to the English: %s' % (n, lang, en[:60]))
            if '  ' in cell or SPACE_BEFORE_PUNCT.search(cell):
                # French typography puts a space before : ; ! ? on purpose.
                if not (lang == 'fr' and re.search(r' [:;!?]', cell) and '  ' not in cell):
                    warns.append('%d %s: double space or space before punctuation: %s' % (n, lang, cell[:60]))
            w = visual_width(cell)
            if w > bud and (lang, en) not in exempt:
                widths.append((w - bud, n, lang, en, cell, w, bud))

    widths.sort(reverse=True)
    if width_only:
        for over, n, lang, en, cell, w, bud in widths:
            sys.stdout.write('%4d over  %5d %-7s %3d/%-3d  %s  ->  %s\n' % (over, n, lang, w, bud, en[:50], cell[:70]))
        sys.stdout.write('%d over budget\n' % len(widths))
        return 0

    for e in errors:
        sys.stdout.write('ERROR  %s\n' % e)
    for w in warns:
        sys.stdout.write('WARN   %s\n' % w)
    for over, n, lang, en, cell, w, bud in widths:
        sys.stdout.write('WIDTH  %d %s: %d wide, budget %d (+%d): %s -> %s\n' % (n, lang, w, bud, over, en[:50], cell[:60]))

    counts = collections.Counter(lang for _, _, lang, _, _, _, _ in widths)
    sys.stdout.write('\n%d error(s), %d warning(s), %d over width' % (len(errors), len(warns), len(widths)))
    if counts:
        sys.stdout.write(' (' + ', '.join('%s %d' % kv for kv in sorted(counts.items())) + ')')
    sys.stdout.write('\n')
    return 1 if (strict and errors) else 0


if __name__ == '__main__':
    sys.exit(main())
