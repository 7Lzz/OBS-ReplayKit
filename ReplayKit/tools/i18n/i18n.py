"""the translation tool of replaykit: extract the english strings into catalog.json, seed each language from the obs locale files, write the numbered list of what each language still misses (todo), merge the translated lines into resources/locale/<code>.json and check every file."""
import argparse
import glob
import html.parser
import io
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))
CATALOG = os.path.join(HERE, 'catalog.json')
SEEDS = os.path.join(HERE, 'seeds')
CHUNKS = os.path.join(HERE, 'chunks')
PARTS = os.path.join(HERE, 'parts')
TODO = os.path.join(HERE, 'todo')
DONE = os.path.join(HERE, 'done')
LOCALE_DIR = os.path.join(ROOT, 'resources', 'locale')
OBS_DATA = os.environ.get('OBS_DATA', r'C:\Program Files\obs-studio\data\obs-studio')

TEXT_ATTRS = ('title', 'placeholder', 'aria-label', 'data-tooltip', 'alt')
TEXT_SKIP_TAGS = {'script', 'style', 'textarea', 'title', 'noscript'}
PLACEHOLDER = re.compile(r'\{[A-Za-z0-9_]+\}')
# a word of at least two letters, so lone symbols, numbers and single letters never count as ui text
WORD = re.compile(r'[A-Za-z]{2}')
UI_CONTEXT = re.compile(r'(label|title|text|textContent|innerText|heading|blurb|tip|tooltip|placeholder|aria-label|ariaLabel|setStatus|status|message|note|caption|hint|name|RK_T|showError|showToast|confirm|toast)\b[^;{}]*$')
CODEISH = re.compile(r'[{};<>\\]|=>|==|&&|\|\||\$\{|^\s*[.#\[]|^[a-z-]+:|\bfunction\b|\bvar\b|\bconst\b|\breturn\b')


def read(path):
    with io.open(path, encoding='utf-8', errors='replace') as f:
        return f.read()


def ui_text(s):
    s = s.strip()
    return bool(s) and len(s) <= 400 and WORD.search(s) is not None and '{{' not in s and not s.startswith('<!--')


class MarkupText(html.parser.HTMLParser):
    """collects the text and text attributes the page runtime translates, skipping what it skips."""

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.found = []
        self.stack = []

    def skipping_text(self):
        return any(tag in TEXT_SKIP_TAGS or no or editable for tag, no, editable in self.stack)

    def skipping_all(self):
        return any(no for _, no, _ in self.stack)

    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        no = a.get('translate') == 'no'
        editable = a.get('contenteditable') in ('', 'true')
        if not self.skipping_all() and not no:
            for name in TEXT_ATTRS:
                v = a.get(name)
                if v and ui_text(v):
                    self.found.append(v.strip())
        if tag not in ('br', 'img', 'input', 'meta', 'link', 'hr', 'source', 'col', 'area', 'base', 'wbr', 'path', 'circle', 'rect', 'line', 'polyline', 'polygon', 'use', 'stop', 'ellipse'):
            self.stack.append((tag, no, editable))

    def handle_startendtag(self, tag, attrs):
        a = dict(attrs)
        if not self.skipping_all() and a.get('translate') != 'no':
            for name in TEXT_ATTRS:
                v = a.get(name)
                if v and ui_text(v):
                    self.found.append(v.strip())

    def handle_endtag(self, tag):
        for i in range(len(self.stack) - 1, -1, -1):
            if self.stack[i][0] == tag:
                del self.stack[i:]
                return

    def handle_data(self, data):
        if not self.skipping_text() and ui_text(data):
            self.found.append(data.strip())


def markup_strings(markup):
    p = MarkupText()
    try:
        p.feed(markup)
        p.close()
    except Exception:
        pass
    return p.found


def js_literals(code):
    """yields (value, before, after) for every string literal of a script, skipping comments and regular expressions; template literals with ${} are skipped since their text is built at run time."""
    i, n = 0, len(code)
    last_sig = ''
    while i < n:
        c = code[i]
        if c == '/' and i + 1 < n and code[i + 1] == '/':
            j = code.find('\n', i)
            i = n if j < 0 else j
            continue
        if c == '/' and i + 1 < n and code[i + 1] == '*':
            j = code.find('*/', i + 2)
            i = n if j < 0 else j + 2
            continue
        if c == '/' and (last_sig == '' or last_sig in '(,=:[!&|?{};+-*%~^<>'):
            j = i + 1
            in_class = False
            while j < n and code[j] != '\n':
                if code[j] == '\\':
                    j += 2
                    continue
                if code[j] == '[':
                    in_class = True
                elif code[j] == ']':
                    in_class = False
                elif code[j] == '/' and not in_class:
                    break
                j += 1
            i = j + 1
            last_sig = 'r'
            continue
        if c in '"\'`':
            q = c
            j = i + 1
            buf = []
            dynamic = False
            while j < n and code[j] != q:
                if code[j] == '\\' and j + 1 < n:
                    esc = code[j + 1]
                    # \uXXXX and \xXX name a character, the way the browser reads the literal
                    width = 4 if esc == 'u' else 2 if esc == 'x' else 0
                    digits = code[j + 2:j + 2 + width]
                    if width and len(digits) == width and all(ch in '0123456789abcdefABCDEF' for ch in digits):
                        buf.append(chr(int(digits, 16)))
                        j += 2 + width
                        continue
                    buf.append({'n': '\n', 't': '\t', 'r': ''}.get(esc, esc))
                    j += 2
                    continue
                if q == '`' and code[j] == '$' and j + 1 < n and code[j + 1] == '{':
                    dynamic = True
                buf.append(code[j])
                j += 1
            value = ''.join(buf)
            before = code[max(0, i - 160):i]
            after = code[j + 1:j + 40]
            if not dynamic:
                yield value, before, after
            i = j + 1
            last_sig = 's'
            continue
        if not c.isspace():
            last_sig = c
        i += 1


def js_strings(code):
    found = []
    for value, before, after in js_literals(code):
        b = before.rstrip()
        a = after.lstrip()
        explicit = re.search(r'RK_T\(\s*$', b) is not None
        if explicit:
            if ui_text(value):
                found.append(value.strip())
            continue
        if value.lstrip().startswith('"') or re.search(r'\bsans-serif\b', value) or re.fullmatch(r'\d+(?:px)? \d+px', value.strip()):
            continue
        if re.search(r'\bvec[234]\s*\(', value) or '||' in value:
            continue
        if '<' in value and '>' in value:
            found.extend(markup_strings(value))
            continue
        # a piece glued to other text at run time never matches a whole text on the page
        if b.endswith('+') or a.startswith('+'):
            continue
        if not ui_text(value) or CODEISH.search(value):
            continue
        v = value.strip()
        if ' ' in v:
            if re.match(r'^[A-Z0-9"\'(¡¿…]', v) and not re.match(r'^[A-Z0-9_]+( [A-Z0-9_]+)*$', v):
                found.append(v)
        elif re.match(r"^[A-Z][a-z]+(?:[-'][A-Za-z]+)*[.!?…:]?$", v):
            line_start = max(b.rfind('\n'), b.rfind(';'), b.rfind('{'))
            if UI_CONTEXT.search(b[line_start + 1:]):
                found.append(v)
    return found


def html_file_strings(text):
    found = []
    scripts = []

    def keep_script(m):
        scripts.append(m.group(1))
        return ''
    markup = re.sub(r'<script(?![^>]*\bsrc=)[^>]*>([\s\S]*?)</script>', keep_script, text, flags=re.I)
    markup = re.sub(r'<style[^>]*>[\s\S]*?</style>', '', markup, flags=re.I)
    found.extend(markup_strings(markup))
    for s in scripts:
        found.extend(js_strings(s))
    return found


CS_MESSAGE = re.compile(r'(?:\["(?:message|error|status|detail|title|label)"\]\s*=\s*|Message\s*=\s*|Fail\(\s*|Exception\(\s*|warnings\.Add\(\s*|\bmsg\s*=\s*|restartReason\s*=\s*|Locales\.T\(\s*|SetupLocale\.T\(\s*)"((?:[^"\\]|\\.)+)"(?=\s*[,);}])')
# the installer puts the title and detail of every step it shows into its page straight from c#, so every sentence of its code counts
CS_ANY_LITERAL = re.compile(r'"((?:[^"\\]|\\.)+)"')
# c++ joins string literals that stand next to each other, so a long text split over several lines is one string
CPP_TR = re.compile(r'\bTr\(\s*((?:"(?:[^"\\]|\\.)*"\s*)+)')
CPP_LITERAL = re.compile(r'"((?:[^"\\]|\\.)*)"')


def whole_cs_argument(line, m):
    """a literal that is a whole value of its own: not interpolated ($"..."), not glued to another piece with +, not a path."""
    before = line[:m.start()].rstrip()
    after = line[m.end():].lstrip()
    if before.endswith('$') or before.endswith('@') or before.endswith('$@') or before.endswith('+') or after.startswith('+'):
        return False
    if ':\\' in m.group(1) or m.group(1).startswith('C:'):
        return False
    return before.endswith(('(', ',', '=', '?', ':', 'return')) and after[:1] in (',', ')', ';', ':', '?', '}')


def unescape_c(s):
    return s.replace('\\"', '"').replace('\\\\', '\\').replace('\\n', '\n')


def extract(args):
    where = {}

    def add(text, origin):
        text = text.strip()
        if not ui_text(text):
            return
        where.setdefault(text, [])
        if origin not in where[text] and len(where[text]) < 3:
            where[text].append(origin)

    for path in sorted(glob.glob(os.path.join(ROOT, 'ui', '*.html')) + glob.glob(os.path.join(ROOT, 'apps', 'setup', '*.html'))):
        name = os.path.basename(path)
        if name == '_i18n.html':
            continue
        for s in html_file_strings(read(path)):
            add(s, name)
    for path in sorted(glob.glob(os.path.join(ROOT, 'ui', '*.js'))):
        name = os.path.basename(path)
        if name == 'helper-endpoint.js':
            continue
        for s in js_strings(read(path)):
            add(s, name)
    for path in sorted(glob.glob(os.path.join(ROOT, 'apps', '**', '*.cs'), recursive=True)):
        if os.sep + 'obj' + os.sep in path or os.sep + 'bin' + os.sep in path:
            continue
        name = os.path.relpath(path, ROOT).replace('\\', '/')
        setup = name.startswith('apps/setup/')
        for line in read(path).splitlines():
            if 'Log.Write' in line or 'SetupLog' in line or 'Console.' in line or 'LogLine(' in line:
                continue
            for m in (CS_ANY_LITERAL if setup else CS_MESSAGE).finditer(line):
                if setup and not whole_cs_argument(line, m):
                    continue
                v = unescape_c(m.group(1))
                if ' ' in v and re.match(r'^[A-Z]', v):
                    add(v, name)
    for path in sorted(glob.glob(os.path.join(ROOT, 'plugins', 'replaykit', 'src', '**', '*.cpp'), recursive=True)):
        name = os.path.relpath(path, ROOT).replace('\\', '/')
        for m in CPP_TR.finditer(read(path)):
            add(unescape_c(''.join(CPP_LITERAL.findall(m.group(1)))), name)

    catalog = {k: where[k] for k in sorted(where, key=lambda s: (s.lower(), s))}
    with io.open(CATALOG, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(catalog, f, ensure_ascii=False, indent=1)
        f.write('\n')
    words = sum(len(k.split()) for k in catalog)
    print('catalog: %d strings, %d words -> %s' % (len(catalog), words, os.path.relpath(CATALOG, ROOT)))


def load_catalog():
    with io.open(CATALOG, encoding='utf-8') as f:
        return json.load(f)


def supported_locales():
    shared = read(os.path.join(ROOT, 'shared', 'LanguageMatch.cs'))
    return set(re.search(r'const\s+string\s+SupportedCodes\s*=\s*"([^"]+)"', shared).group(1).split(','))


def obs_locales():
    supported = supported_locales()
    codes = []
    code = None
    for line in read(os.path.join(OBS_DATA, 'locale.ini')).splitlines():
        s = line.strip()
        if s.startswith('[') and s.endswith(']'):
            code = s[1:-1]
        elif s.startswith('Name=') and code in supported and code not in codes:
            codes.append(code)
    return codes


def read_obs_ini(path):
    out = {}
    if not os.path.exists(path):
        return out
    for line in read(path).splitlines():
        m = re.match(r'^([A-Za-z0-9_.\-]+)="(.*)"\s*$', line.strip())
        if m:
            out[m.group(1)] = m.group(2).replace('\\"', '"')
    return out


def strip_accelerator(s):
    return re.sub(r'&(?!&)', '', s).replace('&&', '&')


def seed(args):
    catalog = load_catalog()
    english = read_obs_ini(os.path.join(OBS_DATA, 'locale', 'en-US.ini'))
    by_text = {}
    for key, value in english.items():
        # the name of the language itself and the names of keyboard keys mean something else in replaykit (home is a page there, not the key)
        if '%' in value or '\\n' in value or key == 'Language' or key.startswith('Hotkeys.'):
            continue
        by_text.setdefault(strip_accelerator(value).strip(), key)
    os.makedirs(SEEDS, exist_ok=True)
    total = 0
    for code in obs_locales():
        if code == 'en-US':
            continue
        theirs = read_obs_ini(os.path.join(OBS_DATA, 'locale', code + '.ini'))
        seeded = {}
        for text in catalog:
            key = by_text.get(text)
            if key and theirs.get(key):
                seeded[text] = strip_accelerator(theirs[key]).strip()
        with io.open(os.path.join(SEEDS, code + '.json'), 'w', encoding='utf-8', newline='\n') as f:
            json.dump(seeded, f, ensure_ascii=False, indent=1, sort_keys=True)
        total += len(seeded)
    print('seeded %d strings over %d languages from the obs locale files' % (total, len(obs_locales()) - 1))


def chunks(args):
    catalog = load_catalog()
    keys = list(catalog)
    size = args.size
    os.makedirs(CHUNKS, exist_ok=True)
    for old in glob.glob(os.path.join(CHUNKS, '*.json')):
        os.remove(old)
    count = 0
    for i in range(0, len(keys), size):
        count += 1
        part = {k: ', '.join(catalog[k]) for k in keys[i:i + size]}
        with io.open(os.path.join(CHUNKS, 'chunk_%02d.json' % count), 'w', encoding='utf-8', newline='\n') as f:
            json.dump(part, f, ensure_ascii=False, indent=1)
    print('%d chunks of up to %d strings in %s' % (count, size, os.path.relpath(CHUNKS, ROOT)))


# the obs terms a translator needs to word replaykit the way obs words itself in that language
GLOSSARY_TERMS = [
    'Replay Buffer', 'Start Replay Buffer', 'Stop Replay Buffer', 'Save Replay', 'Scene', 'Scenes', 'Source', 'Sources',
    'Settings', 'Hotkeys', 'Output', 'Audio', 'Video', 'Advanced', 'General', 'Recording', 'Start Recording', 'Stop Recording',
    'Pause Recording', 'Recording Path', 'Recording Quality', 'Recording Format', 'Encoder', 'Bitrate', 'Preview', 'Program',
    'Projector', 'Studio Mode', 'Virtual Camera', 'Start Virtual Camera', 'Filters', 'Display Capture', 'Game Capture',
    'Window Capture', 'Desktop Audio', 'Mic/Aux', 'Audio Mixer', 'Profile', 'Scene Collection', 'Docks', 'Theme', 'Appearance',
    'Stream', 'Start Streaming', 'Volume', 'Mute', 'Downscale Filter', 'Restart', 'Exit', 'Apply', 'Cancel', 'OK', 'Yes', 'No',
    'Browse', 'Remove', 'Delete', 'Rename', 'Properties', 'Lock', 'Show', 'Hide', 'Copy', 'Paste', 'Help', 'Default',
]


def escape_line(s):
    return s.replace('\\', '\\\\').replace('\t', '\\t').replace('\n', '\\n').replace('\r', '')


def unescape_line(s):
    out = []
    i = 0
    while i < len(s):
        c = s[i]
        if c == '\\' and i + 1 < len(s):
            nxt = s[i + 1]
            out.append({'n': '\n', 't': '\t', '\\': '\\'}.get(nxt, '\\' + nxt))
            i += 2
            continue
        out.append(c)
        i += 1
    return ''.join(out)


def current_table(code):
    table = {}
    target = os.path.join(LOCALE_DIR, code + '.json')
    if os.path.exists(target):
        with io.open(target, encoding='utf-8') as f:
            table.update(json.load(f))
    seed_file = os.path.join(SEEDS, code + '.json')
    if os.path.exists(seed_file):
        with io.open(seed_file, encoding='utf-8') as f:
            table.update(json.load(f))
    return table


def todo(args):
    catalog = load_catalog()
    codes = args.codes or [c for c in obs_locales() if c != 'en-US']
    unsupported = set(codes) - (supported_locales() - {'en-US'})
    if unsupported:
        sys.exit('Unsupported translation locales: ' + ', '.join(sorted(unsupported)))
    english = read_obs_ini(os.path.join(OBS_DATA, 'locale', 'en-US.ini'))
    term_keys = {}
    for key, value in english.items():
        if key == 'Language' or key.startswith('Hotkeys.'):
            continue
        text = strip_accelerator(value).strip()
        if text in GLOSSARY_TERMS and text not in term_keys:
            term_keys[text] = key
    for code in codes:
        folder = os.path.join(TODO, code)
        done = os.path.join(DONE, code)
        # the numbers of a todo list are what the translated lines refer to, so a list is never rewritten under translated lines that still wait to be merged
        if os.path.isdir(done) and glob.glob(os.path.join(done, '*.txt')) and not args.force:
            print('%s: translated lines wait in %s, merge them first (or pass --force)' % (code, os.path.relpath(done, ROOT)))
            continue
        for old in glob.glob(os.path.join(folder, '*.txt')) + glob.glob(os.path.join(done, '*.txt')):
            os.remove(old)
        os.makedirs(folder, exist_ok=True)
        have = current_table(code)
        missing = [k for k in catalog if k not in have]
        for start in range(0, len(missing), args.size):
            lines = []
            for n, text in enumerate(missing[start:start + args.size], start + 1):
                hint = catalog[text][0] if catalog[text] else ''
                lines.append('%d\t%s\t[%s]' % (n, escape_line(text), hint))
            with io.open(os.path.join(folder, 'part_%02d.txt' % (start // args.size + 1)), 'w', encoding='utf-8', newline='\n') as f:
                f.write('\n'.join(lines) + '\n')
        theirs = read_obs_ini(os.path.join(OBS_DATA, 'locale', code + '.ini'))
        gloss = ['%s = %s' % (t, strip_accelerator(theirs[k]).strip()) for t, k in sorted(term_keys.items()) if theirs.get(k)]
        with io.open(os.path.join(folder, 'glossary.txt'), 'w', encoding='utf-8', newline='\n') as f:
            f.write('\n'.join(gloss) + '\n')
        print('%s: %d missing in %d parts' % (code, len(missing), (len(missing) + args.size - 1) // args.size))


def done_lines(code):
    """the translated lines of a language, mapped back to their english text through the todo list they answer."""
    numbered = {}
    for path in glob.glob(os.path.join(TODO, code, 'part_*.txt')):
        for line in read(path).splitlines():
            cols = line.split('\t')
            if len(cols) >= 2 and cols[0].strip().isdigit():
                numbered[int(cols[0])] = unescape_line(cols[1])
    out = {}
    for path in sorted(glob.glob(os.path.join(DONE, code, '*.txt'))):
        for line in read(path).splitlines():
            cols = line.split('\t')
            if len(cols) < 2 or not cols[0].strip().isdigit():
                continue
            n = int(cols[0].strip())
            if n in numbered:
                out[numbered[n]] = unescape_line(cols[1].strip())
    return out


def problems(english, translated):
    if not isinstance(translated, str) or not translated.strip():
        return 'empty'
    if sorted(PLACEHOLDER.findall(english)) != sorted(PLACEHOLDER.findall(translated)):
        return 'placeholders differ'
    if '<' in translated and '<' not in english:
        return 'markup added'
    # a file dialog filter is description|pattern, so a | inside a translated description would break it
    if '|' in translated and '|' not in english:
        return 'a | was added'
    return None


def merge(args):
    catalog = load_catalog()
    codes = args.codes or sorted({os.path.basename(p) for p in glob.glob(os.path.join(PARTS, '*')) + glob.glob(os.path.join(DONE, '*')) if os.path.isdir(p)})
    unsupported = set(codes) - (supported_locales() - {'en-US'})
    if unsupported:
        sys.exit('Unsupported translation locales: ' + ', '.join(sorted(unsupported)))
    os.makedirs(LOCALE_DIR, exist_ok=True)
    for code in codes:
        if not re.match(r'^[a-z]{2,3}-[A-Za-z]{2,4}$', code):
            print('skip %s: not a language code' % code)
            continue
        table = {}
        target = os.path.join(LOCALE_DIR, code + '.json')
        # what an earlier merge already holds stays unless a newer part replaces it
        if os.path.exists(target):
            with io.open(target, encoding='utf-8') as f:
                table.update(json.load(f))
        seed_file = os.path.join(SEEDS, code + '.json')
        if os.path.exists(seed_file):
            with io.open(seed_file, encoding='utf-8') as f:
                table.update(json.load(f))
        bad = []
        for part in sorted(glob.glob(os.path.join(PARTS, code, '*.json'))):
            try:
                with io.open(part, encoding='utf-8') as f:
                    data = json.load(f)
            except ValueError as e:
                bad.append('%s: %s' % (os.path.basename(part), e))
                continue
            for k, v in data.items():
                if k in catalog:
                    issue = problems(k, v)
                    if issue:
                        bad.append('%r: %s' % (k[:60], issue))
                    else:
                        table[k] = v
        for k, v in done_lines(code).items():
            if k in catalog:
                issue = problems(k, v)
                if issue:
                    bad.append('%r: %s' % (k[:60], issue))
                else:
                    table[k] = v
        table = {k: v for k, v in table.items() if k in catalog and problems(k, v) is None}
        with io.open(target, 'w', encoding='utf-8', newline='\n') as f:
            json.dump(table, f, ensure_ascii=False, indent=1, sort_keys=True)
            f.write('\n')
        missing = len(catalog) - len(table)
        print('%s: %d/%d translated, %d missing, %d rejected' % (code, len(table), len(catalog), missing, len(bad)))
        for b in bad[:10]:
            print('   rejected ' + b)


def verify(args):
    """checks the answered parts of one translator without merging: every line of each todo part has an answer that keeps its placeholders."""
    code = args.code
    failed = False
    for number in args.parts:
        name = 'part_%02d.txt' % int(number)
        todo_path = os.path.join(TODO, code, name)
        done_path = os.path.join(DONE, code, name)
        if not os.path.exists(todo_path):
            print('%s: no such todo part' % name)
            failed = True
            continue
        wanted = {}
        for line in read(todo_path).splitlines():
            cols = line.split('\t')
            if len(cols) >= 2 and cols[0].strip().isdigit():
                wanted[int(cols[0])] = unescape_line(cols[1])
        answered = {}
        if os.path.exists(done_path):
            for line in read(done_path).splitlines():
                cols = line.split('\t')
                if len(cols) >= 2 and cols[0].strip().isdigit():
                    answered[int(cols[0].strip())] = unescape_line(cols[1].strip())
        missing = sorted(n for n in wanted if n not in answered)
        bad = [(n, problems(wanted[n], answered[n])) for n in sorted(answered) if n in wanted and problems(wanted[n], answered[n])]
        print('%s: %d/%d answered, missing %s, invalid %s' % (name, len(wanted) - len(missing), len(wanted), missing[:20] or 'none', ['%d (%s)' % b for b in bad[:20]] or 'none'))
        failed = failed or bool(missing) or bool(bad)
    sys.exit(1 if failed else 0)


def check(args):
    catalog = load_catalog()
    failed = False
    expected = supported_locales() - {'en-US'}
    found = set()
    for path in sorted(glob.glob(os.path.join(LOCALE_DIR, '*.json'))):
        code = os.path.splitext(os.path.basename(path))[0]
        found.add(code)
        try:
            with io.open(path, encoding='utf-8') as f:
                table = json.load(f)
        except ValueError as e:
            print('%s: not valid json: %s' % (code, e))
            failed = True
            continue
        if not isinstance(table, dict):
            print('%s: not a json object' % code)
            failed = True
            continue
        issues = [(k, problems(k, v)) for k, v in table.items() if problems(k, v)]
        stale = [k for k in table if k not in catalog]
        missing = [k for k in catalog if k not in table]
        print('%s: %d translated, %d missing, %d no longer used, %d invalid' % (code, len(table) - len(stale), len(missing), len(stale), len(issues)))
        if issues or missing or stale:
            failed = True
            for k, why in issues[:5]:
                print('   %r: %s' % (k[:60], why))
    if found != expected:
        print('missing locale files: %s; unsupported files: %s' % (sorted(expected - found), sorted(found - expected)))
        failed = True
    sys.exit(1 if failed else 0)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    sub = p.add_subparsers(dest='cmd', required=True)
    sub.add_parser('extract').set_defaults(fn=extract)
    sub.add_parser('seed').set_defaults(fn=seed)
    c = sub.add_parser('chunks')
    c.add_argument('--size', type=int, default=200)
    c.set_defaults(fn=chunks)
    t = sub.add_parser('todo')
    t.add_argument('codes', nargs='*')
    t.add_argument('--size', type=int, default=150)
    t.add_argument('--force', action='store_true')
    t.set_defaults(fn=todo)
    m = sub.add_parser('merge')
    m.add_argument('codes', nargs='*')
    m.set_defaults(fn=merge)
    sub.add_parser('check').set_defaults(fn=check)
    v = sub.add_parser('verify')
    v.add_argument('code')
    v.add_argument('parts', nargs='+')
    v.set_defaults(fn=verify)
    args = p.parse_args()
    args.fn(args)


if __name__ == '__main__':
    main()
