# -*- coding: utf-8 -*-
"""Generates Core/Localization.Tables.cs from strings.tsv.

WHY THERE IS A GENERATOR AT ALL. The tables started as four hand-written C# dictionaries, which
is a perfectly good shape for four languages and a bad one for thirteen: adding a language meant
a new 700-line block in the same file, every consistency check had to parse C# back into data,
and a wording corrected in one language had no mechanical way of reaching the others. The data
now lives in ONE tab-separated file - one row per English string, one column per language - and
this script is the only thing that writes the C#. Adding a language is a column.

    python Tools/i18n/loc_build.py            regenerate the C#
    python Tools/i18n/loc_build.py --check    fail if the C# is not what the TSV says

THE TSV IS THE SOURCE. Editing Localization.Tables.cs by hand is a mistake the next run silently
undoes, which is why the generated file says so at the top.

An empty cell is not a gap to be filled later - it is a decision. A string with no translation
renders in English, by the design of Loc.T, so leaving a cell empty is how a translation that
cannot fit its control (see the width rule in check_width.py) is deliberately not shipped.
"""
import io, os, sys, collections

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))          # the ClawTweaksCenter repo root
TSV = os.path.join(HERE, 'strings.tsv')
OUT = os.path.join(ROOT, 'ClawTweaksCenter', 'Core', 'Localization.Tables.cs')

# TSV column -> the C# field name for that language's dictionary. The order here is the order the
# blocks appear in the generated file.
LANGS = collections.OrderedDict([
    ('de', 'German'),
    ('fr', 'French'),
    ('ko', 'Korean'),
    ('es', 'Spanish'),
    ('ru', 'Russian'),
    ('el', 'Greek'),
    ('zh-Hans', 'ChineseSimplified'),
    ('zh-Hant', 'ChineseTraditional'),
    ('it', 'Italian'),
    ('pt-BR', 'Portuguese'),
    ('ja', 'Japanese'),
    ('pl', 'Polish'),
])

HEADER = u'''\ufeffusing System.Collections.Generic;

namespace ClawTweaksCenter.Core
{
    public static partial class Loc
    {
        // GENERATED FROM Tools/i18n/strings.tsv BY Tools/i18n/loc_build.py - DO NOT EDIT BY HAND.
        // An edit here survives exactly until the next run of that script. Change the TSV instead,
        // regenerate, and commit both.
        //
        // The tables behind T(). Keyed by the English string; anything absent renders in English,
        // which is what makes leaving a string out a decision rather than a bug.
        //
        // WIDTH-CHECKED. Every entry passed a rendered-width check against its English original: at
        // most 1.7x the English, or five characters more, whichever is larger, with CJK characters
        // counted double because they render about twice as wide. Center's chips, tabs and tiles are
        // sized for the English word and do not grow. A translation that failed the check was
        // SHORTENED until it fit rather than dropped - a half-English Russian screen reads as broken
        // where a half-English German one merely reads as unfinished. The handful that could not be
        // shortened honestly are left empty in the TSV and stay English; Tools/i18n/check_width.py
        // reports them.
        //
        // Menu headings are English on purpose: the Home tiles keep their English titles and only
        // their one-line descriptions are translated. "Library" is the exception, and it is
        // translated everywhere it appears.
        //
        // NOT IN HERE, and not by oversight: date format strings ("d MMM yyyy") and brand names.
        //
        // A HANDFUL OF KEYS DO CARRY {0}, and they are the exception that proves the rule above: an
        // INTERPOLATED string is built at runtime and can never match a key, so those call sites were
        // rewritten to pass the format itself through Loc.F and fill it in afterwards. Translating
        // the format rather than the finished sentence is what lets a language put the game name
        // somewhere other than where English puts it. Everything still built by interpolation stays
        // English, and stays out of here.
'''

FOOTER = u'''
    }
}
'''

LEFT = os.path.join(HERE, 'left-in-english.tsv')


def render_left_in_english():
    """The trailing block DEV_GUIDELINES points at as the answer to "why is this one word still
    English".

    It is DATA, in left-in-english.tsv, and not a hand-written block at the bottom of the C#:
    this generator rewrites that file wholesale, so anything written there by hand survives
    exactly until the next run. It did not survive the first one."""
    try:
        text = io.open(LEFT, encoding='utf-8').read()
    except IOError:
        return u''

    rows = [l.split(u'\t') for l in text.splitlines()[1:] if l.strip()]
    if not rows:
        return u''

    out = [u'\n/*\n'
           u' * LEFT IN ENGLISH ON PURPOSE - the honest translation is wider than the control it\n'
           u' * has to fit in, and could not be shortened without saying something else. This list\n'
           u' * is the answer to "why is this one word still English", so it is kept rather than\n'
           u' * tidied away. GENERATED FROM Tools/i18n/left-in-english.tsv.\n'
           u' *\n']
    for r in rows:
        while len(r) < 6:
            r.append(u'')
        lang, en, honest, width, budget, note = r[0], r[1], r[2], r[3], r[4], r[5]
        out.append(u' *   %-8s "%s" -> "%s" (%s wide, budget %s)\n'
                   % (lang + u':', en, honest, width, budget))
        if note:
            out.append(u' *             %s\n' % note)
    out.append(u' */\n')
    return u''.join(out)


def tsv_unescape(s):
    out, i, n = [], 0, len(s)
    while i < n:
        c = s[i]
        if c == '\\' and i + 1 < n:
            d = s[i + 1]
            out.append({'t': '\t', 'n': '\n', 'r': '\r', '\\': '\\'}.get(d, d))
            i += 2
            continue
        out.append(c)
        i += 1
    return ''.join(out)


def tsv_escape(s):
    return (s.replace('\\', '\\\\').replace('\t', '\\t')
             .replace('\n', '\\n').replace('\r', '\\r'))


def read_tsv(path=TSV):
    """-> (ordered list of English keys, {lang: {english: translation}})"""
    with io.open(path, encoding='utf-8') as f:
        rows = [line.rstrip('\n').rstrip('\r').split('\t') for line in f]
    rows = [r for r in rows if r and r[0].strip()]
    head = rows[0]
    if head[0] != 'english':
        raise SystemExit('strings.tsv: first column must be "english", found %r' % head[0])
    for col in head[1:]:
        if col not in LANGS:
            raise SystemExit('strings.tsv: unknown language column %r' % col)

    keys, tables, seen = [], {c: collections.OrderedDict() for c in head[1:]}, set()
    for n, row in enumerate(rows[1:], start=2):
        row += [''] * (len(head) - len(row))
        key = tsv_unescape(row[0])
        if key in seen:
            raise SystemExit('strings.tsv line %d: duplicate English key %r' % (n, key[:60]))
        seen.add(key)
        keys.append(key)
        for col, cell in zip(head[1:], row[1:]):
            cell = tsv_unescape(cell).strip()
            if cell:
                tables[col][key] = cell
    return keys, tables


def cs_escape(s):
    """C# string literal body, non-ASCII as \\uXXXX so the file survives any encoding it meets."""
    out = []
    for ch in s:
        if ch == '\\':
            out.append('\\\\')
        elif ch == '"':
            out.append('\\"')
        elif ch == '\n':
            out.append('\\n')
        elif ch == '\r':
            out.append('\\r')
        elif ch == '\t':
            out.append('\\t')
        elif ord(ch) < 0x20 or ord(ch) > 0x7E:
            out.append('\\u%04X' % ord(ch))
        else:
            out.append(ch)
    return ''.join(out)


def render(keys, tables):
    parts = [HEADER]
    for col, field in LANGS.items():
        table = tables.get(col) or {}
        parts.append(u'\n        private static readonly Dictionary<string, string> %s '
                     u'= new Dictionary<string, string>\n        {\n' % field)
        for k in keys:
            if k in table:
                parts.append(u'            ["%s"] = "%s",\n' % (cs_escape(k), cs_escape(table[k])))
        parts.append(u'        };\n')
    parts.append(FOOTER)
    parts.append(render_left_in_english())
    return ''.join(parts)


def main():
    keys, tables = read_tsv()
    text = render(keys, tables)

    counts = ', '.join('%s %d' % (c, len(tables.get(c) or {})) for c in LANGS)
    sys.stdout.write('%d English keys; %s\n' % (len(keys), counts))

    if '--check' in sys.argv:
        current = io.open(OUT, encoding='utf-8-sig').read()
        if current.lstrip('\ufeff') == text.lstrip('\ufeff'):
            sys.stdout.write('Localization.Tables.cs is up to date.\n')
            return 0
        sys.stdout.write('Localization.Tables.cs does NOT match strings.tsv - run loc_build.py.\n')
        return 1

    io.open(OUT, 'w', encoding='utf-8', newline='\n').write(text)
    sys.stdout.write('wrote %s\n' % OUT)
    return 0


if __name__ == '__main__':
    sys.exit(main())
