# -*- coding: utf-8 -*-
"""What is on screen that strings.tsv does not have - and what strings.tsv has that is no longer
on screen.

THIS EXISTS BECAUSE THE FIRST TRANSLATION ROUND WAS CURATED BY HAND FOR HOURS. Strings kept
turning up untranslated on the device after each build, and the reason was not carelessness: the
survey that looked for them walked a HAND-MAINTAINED LIST OF 26 FILENAMES (survey3.UI_FILES).
Every screen added since - achievements, friends, notifications, drivers, the FAQ, sound
settings, the quick menu, the footer, the letter bar, wallpapers - was invisible to it. A list of
files you have to remember to update is not a survey, it is a second place to forget something.

So this one walks EVERY .cs file under the project and filters by what the literal looks like and
where it sits, not by which file it is in. New screen, new file, still found.

    python Tools/i18n/loc_coverage.py                summary
    python Tools/i18n/loc_coverage.py --gaps         every untranslated candidate, with location
    python Tools/i18n/loc_coverage.py --stale        keys no source file mentions any more
    python Tools/i18n/loc_coverage.py --gaps-tsv F   write the gaps as TSV rows ready to fill in

THREE BUCKETS, and the middle one is the whole point:

  translated   the literal is a key in strings.tsv. Nothing to do.
  CANDIDATE    it looks like interface text and sits where interface text sits, but no key
               matches. Either it needs translating, or it needs a reason not to be.
  ignored      it does not look like interface text (paths, registry keys, format plumbing,
               PowerShell, XAML resource names). Reported as a count so the filter itself can be
               audited rather than trusted.

A CANDIDATE IS NOT AUTOMATICALLY A BUG. Some literals reach the screen through a builder that
does not call Loc.T, and those need a code change before any translation could show - the report
says which ones those are, because that is a different job from filling in a table.
"""
import io, os, re, sys, glob, collections

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
SRC_GLOB = os.path.join(ROOT, 'ClawTweaksCenter', '**', '*.cs')

sys.path.insert(0, HERE)
from loc_build import read_tsv  # noqa: E402


# ---------------------------------------------------------------------------------------------
# Literal extraction. A plain regex desynchronises on the first quote inside a // comment and
# then reports the CODE between literals as if it were text, so this walks the file as a small
# state machine. Adjacent literals joined by + are one runtime string, and the table is keyed by
# the runtime string.
# ---------------------------------------------------------------------------------------------

def literals(src):
    """Yield (line, value, start_offset, end_offset) for every "..." and @"..." literal.

    The START is carried rather than recomputed. Working it back from the end and the value's
    length is off by one for every literal, and off by more for every literal containing an
    escape - which made the "is this the argument of Loc.T?" test look at the wrong characters
    and report zero wrapped literals in a codebase full of them.
    """
    out = []
    i, n, line = 0, len(src), 1
    while i < n:
        c = src[i]
        if c == '\n':
            line += 1
            i += 1
            continue
        if c == '/' and i + 1 < n and src[i + 1] == '/':
            while i < n and src[i] != '\n':
                i += 1
            continue
        if c == '/' and i + 1 < n and src[i + 1] == '*':
            i += 2
            while i + 1 < n and not (src[i] == '*' and src[i + 1] == '/'):
                if src[i] == '\n':
                    line += 1
                i += 1
            i += 2
            continue
        if c == "'":                                        # char literal
            i += 1
            if i < n and src[i] == '\\':
                i += 1
            i += 2
            continue
        if c == '@' and i + 1 < n and src[i + 1] == '"':    # verbatim
            begin = i
            i += 2
            start, buf = line, []
            while i < n:
                if src[i] == '"':
                    if i + 1 < n and src[i + 1] == '"':
                        buf.append('"')
                        i += 2
                        continue
                    i += 1
                    break
                if src[i] == '\n':
                    line += 1
                buf.append(src[i])
                i += 1
            out.append((start, ''.join(buf), begin, i))
            continue
        if c == '"':
            begin = i
            i += 1
            start, buf = line, []
            while i < n and src[i] != '"':
                if src[i] == '\\' and i + 1 < n:
                    esc = src[i + 1]
                    if esc == 'u' and i + 5 < n:
                        try:
                            buf.append(chr(int(src[i + 2:i + 6], 16)))
                            i += 6
                            continue
                        except ValueError:
                            pass
                    buf.append({'n': '\n', 't': '\t', 'r': '\r'}.get(esc, esc))
                    i += 2
                    continue
                if src[i] == '\n':
                    line += 1
                buf.append(src[i])
                i += 1
            i += 1
            out.append((start, ''.join(buf), begin, i))
            continue
        i += 1
    return out


def join_adjacent(src, lits):
    """Merge a + b into one value: a sentence split over three source lines is one string."""
    merged = []
    for ln, val, begin, end in lits:
        if merged and src[merged[-1][3]:begin].strip() == '+':
            p = merged[-1]
            merged[-1] = (p[0], p[1] + val, p[2], end)
            continue
        merged.append((ln, val, begin, end))
    return merged


# ---------------------------------------------------------------------------------------------
# Is this interface text?
#
# Deliberately LOOSER than the first survey, which required a space and so threw away every
# single-word label - "Back", "Cancel", "All", "Off" are all on screen and all in the table. It
# also threw away everything containing "{", which is exactly the shape of the format keys that
# were introduced so a language could put the game name somewhere else.
#
# A false positive costs one line in a report. A false negative is a string that ships
# untranslated and is found on the device, which is the failure this file exists to prevent.
# ---------------------------------------------------------------------------------------------

NOT_UI_SUBSTRINGS = (
    '.exe', '.dll', '.json', '.xaml', '.zip', '.nupkg', '.png', '.jpg', '.log', '.tmp', '.ps1',
    'Software\\', 'HKEY', 'HKLM', 'HKCU', 'CN=', '://', 'pack:', '\\\\', '/c ', '--', '==',
    'Get-', 'Set-', 'Add-', 'New-', 'Remove-', 'Select-', 'ForEach-', 'Where-', 'Start-',
    'Segoe', 'Consolas', '#FF', '0x', '{Binding', 'System.', 'Microsoft.', 'Windows.',
    'AppData', 'ProgramData', '%LOCALAPPDATA%', 'steam://', 'com.', 'org.',
)
NOT_UI_EXACT = {
    'True', 'False', 'None', 'Auto', 'null', 'true', 'false', 'OK', 'Yes', 'No',
}
# A literal that is nothing but a token: no spaces, and cased like an identifier or a constant.
IDENTIFIERISH = re.compile(r'^[A-Za-z][A-Za-z0-9_]*$')
CONSTANTISH = re.compile(r'^[A-Z0-9_]{2,}$')


def looks_ui(v):
    v = v.strip()
    if len(v) < 2 or len(v) > 400:
        return False
    if v in NOT_UI_EXACT:
        return False
    for bad in NOT_UI_SUBSTRINGS:
        if bad in v:
            return False
    if not (v[0].isalpha() or v[0] in u'•→…¿¡'):
        return False
    if CONSTANTISH.match(v):
        return False
    # A single lowercase token ("steam", "epic") is a key or an id, not a label. A single
    # capitalised word ("Back") very well may be a label, so it stays in.
    if ' ' not in v and IDENTIFIERISH.match(v) and not v[0].isupper():
        return False
    return True


# ---------------------------------------------------------------------------------------------
# Does it reach a translated builder?
#
# Suffixes rather than exact names: "MaintCard(" once missed BuildMaintCard( and hid three cards
# for an entire build.
# ---------------------------------------------------------------------------------------------

# "wrapped" is decided on the text IMMEDIATELY before the literal, not on a window around it.
# The first version looked 300 characters back and called three resource keys translation gaps
# because a Loc.T( happened to sit on a neighbouring line - Resources["SetupButton"] is a style
# name, and a report that cries wolf three times in twelve is a report nobody finishes reading.
ARGUMENT_OF = ('Loc.T(', 'Loc.F(', 'Core.Loc.T(', 'Core.Loc.F(')

# Places a literal can sit that are never interface text, however English they look.
NOT_TEXT_POSITION = ('Resources[', 'ToString(', 'FindResource(', 'GetValue(', 'nameof(',
                     'StartsWith(', 'EndsWith(', 'Contains(', 'Equals(', 'Split(',
                     'GetString(', 'SetValue(', 'Parse(', 'ParseExact(')
BUILDERS = (
    'Title(', 'Caption(', 'Body(', 'StatusRow(', 'ActionCallout(', 'ToolRow(', 'ModeBanner(',
    'AddAction(', 'Chip(', 'Tile(', 'Tab(', 'SettingRow(', 'LibraryMessage(', 'PromptRow(',
    'InfoLead(', 'InfoHeading(', 'InfoLine(', 'Card(', 'Row(', 'Label(', 'Step(', 'Note(',
    'SetStatus(', 'Header(', 'Message(', 'Prompt(', 'Toast(', 'Banner(', 'Hint(', 'Describe(',
    'Content =', 'Text =', 'Header =', 'ToolTip =', 'Title =', 'Label =',
)


def classify_site(before):
    """'ignore' when the literal sits where text never sits, else how strongly it looks on-screen."""
    # `before` ends with the literal's own opening quote (and, for a verbatim string, the @ too),
    # so those come off before anything is matched against it. Leaving them on made every
    # classification fall through to "loose" and reported zero wrapped literals in a codebase
    # that has hundreds.
    tail = before.rstrip()
    if tail.endswith('"'):
        tail = tail[:-1]
    if tail.endswith('@'):
        tail = tail[:-1]
    tail = tail.rstrip()
    if any(tail.endswith(p) for p in NOT_TEXT_POSITION):
        return 'ignore'
    if any(tail.endswith(c) for c in ARGUMENT_OF):
        return 'wrapped'
    if any(b in before for b in BUILDERS):
        return 'builder'
    return 'loose'


def read_source(path):
    """Not every .cs in this tree is UTF-8 - a few predate the convention and are Windows-1252.

    Reading those with errors='replace' turned every em dash into U+FFFD, so "Rename — " came out
    as "Rename � " and would have been written into the table as a key that can never match the
    string the program actually builds. A silently corrupted key is worse than a crash.
    """
    raw = io.open(path, 'rb').read()
    for enc in ('utf-8-sig', 'cp1252'):
        try:
            return raw.decode(enc)
        except UnicodeDecodeError:
            continue
    return raw.decode('utf-8', errors='replace')


def main():
    keys, _ = read_tsv()
    keyset = set(keys)

    translated, candidates, ignored = 0, collections.OrderedDict(), 0
    mentioned = set()

    files = [f for f in sorted(glob.glob(SRC_GLOB, recursive=True))
             if (os.sep + 'obj' + os.sep) not in f and (os.sep + 'bin' + os.sep) not in f
             and 'Localization' not in os.path.basename(f)]

    for path in files:
        rel = os.path.relpath(path, ROOT).replace('\\', '/')
        src = read_source(path)
        for ln, val, begin, end in join_adjacent(src, literals(src)):
            if val in keyset:
                translated += 1
                mentioned.add(val)
                continue
            if not looks_ui(val):
                ignored += 1
                continue
            before = src[max(0, begin - 300):begin]
            kind = classify_site(before)
            if kind == 'ignore' and val not in candidates:
                ignored += 1
                continue
            rec = candidates.setdefault(val, {'sites': [], 'kind': 'loose'})
            rec['sites'].append('%s:%d' % (rel, ln))
            # The strongest site wins: one call site that clearly puts it on screen settles it,
            # however many incidental ones there are.
            order = {'ignore': 0, 'loose': 1, 'builder': 2, 'wrapped': 3}
            if order[kind] > order[rec['kind']]:
                rec['kind'] = kind

    sys.stdout.write('files scanned                     %d\n' % len(files))
    sys.stdout.write('literals already translated       %d occurrences of %d keys\n'
                     % (translated, len(mentioned)))
    sys.stdout.write('literals ignored as non-UI        %d\n' % ignored)
    sys.stdout.write('CANDIDATES (look like UI, no key) %d\n' % len(candidates))
    for kind, label in (('wrapped', 'inside Loc.T/Loc.F - MUST be translated'),
                        ('builder', 'reaches a known builder - very likely on screen'),
                        ('loose',   'looks like text but no builder nearby - needs an eye')):
        n = sum(1 for r in candidates.values() if r['kind'] == kind)
        sys.stdout.write('    %-8s %5d   %s\n' % (kind, n, label))

    stale = [k for k in keys if k not in mentioned]
    sys.stdout.write('keys no source file mentions      %d\n' % len(stale))

    if '--gaps' in sys.argv:
        sys.stdout.write('\n=== candidates ===\n')
        for kind in ('wrapped', 'builder', 'loose'):
            rows = [(v, r) for v, r in candidates.items() if r['kind'] == kind]
            sys.stdout.write('\n-- %s (%d)\n' % (kind, len(rows)))
            for v, r in rows:
                sys.stdout.write('%-34s %s\n' % (r['sites'][0], v.replace('\n', '\\n')[:150]))

    if '--stale' in sys.argv:
        sys.stdout.write('\n=== keys no source file mentions (%d) ===\n' % len(stale))
        for k in stale:
            sys.stdout.write('  %s\n' % k.replace('\n', '\\n')[:150])

    if '--gaps-tsv' in sys.argv:
        dest = sys.argv[sys.argv.index('--gaps-tsv') + 1]
        with io.open(dest, 'w', encoding='utf-8', newline='\n') as f:
            for kind in ('wrapped', 'builder', 'loose'):
                for v, r in candidates.items():
                    if r['kind'] == kind:
                        f.write(u'%s\t%s\t%s\n' % (kind, r['sites'][0],
                                                   v.replace('\\', '\\\\').replace('\n', '\\n')
                                                    .replace('\t', '\\t')))
        sys.stdout.write('\nwrote %s\n' % dest)

    return 0


if __name__ == '__main__':
    sys.exit(main())
