"""Validate and package ReplayKit's messages."""
import argparse
import hashlib
import json
from pathlib import Path
import re

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
SOURCE = HERE / "messages"
ID = re.compile(r"[a-z][a-zA-Z0-9]*(?:\.[a-z][a-zA-Z0-9]*)+")
PLACEHOLDER = re.compile(r"\{[a-zA-Z0-9_]+\}")
PLURALS = {
    "en": {"one", "other"}, "de": {"one", "other"}, "es": {"one", "many", "other"},
    "fr": {"one", "many", "other"}, "ja": {"other"}, "pt": {"one", "many", "other"},
    "ru": {"one", "few", "many", "other"}, "zh": {"other"},
}
CALL = re.compile(r'\b(RK_MH?|(?:Locales|SetupLocale)\.Message|SetupLocale\.Pair|SetupLocale\.Error|TrMessage|replaykit_message)\(\s*"([^"]+)"')
# an id the checker cannot read could name a message that does not exist, which would show the raw id as text
COMPUTED_CALL = re.compile(r'(?<!QString )(?<!function )\b(?:RK_MH?|(?:Locales|SetupLocale)\.Message|SetupLocale\.Pair|SetupLocale\.Error|TrMessage|replaykit_message)\(\s*(?!")')
ATTRIBUTE = re.compile(r'\bdata-i18n(?:-title|-placeholder|-aria-label|-data-tooltip|-alt)?\s*=\s*(["\'])([^"\']+)\1')
# the english-keyed lookups the messages replaced; one that comes back would show english text again
LEGACY_CALL = re.compile(r'\b(?:RK_T|(?:Locales|SetupLocale)\.T|Tr)\(')


def unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate key {key!r}")
        result[key] = value
    return result


def read(path):
    with path.open(encoding="utf-8") as stream:
        return json.load(stream, object_pairs_hook=unique)


def write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    output = json.dumps(data, ensure_ascii=False, indent=2, sort_keys=True) + "\n"
    if not path.exists() or path.read_text(encoding="utf-8") != output:
        path.write_text(output, encoding="utf-8", newline="\n")


def languages():
    source = (ROOT / "shared/LanguageMatch.cs").read_text(encoding="utf-8")
    return re.search(r'SupportedCodes\s*=\s*"([^"]+)"', source)[1].split(",")


def revision(text):
    serialized = json.dumps(text, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(serialized.encode()).hexdigest()[:16]


def variants(text):
    return text.values() if isinstance(text, dict) else [text]


def problem(text, plural, language, placeholders=None):
    if plural:
        if not isinstance(text, dict) or set(text) != PLURALS[language.split("-")[0]]:
            return "wrong plural categories"
    elif not isinstance(text, str):
        return "expected plain text"
    for value in variants(text):
        if not isinstance(value, str) or not value.strip() or "<" in value or ">" in value:
            return "expected non-empty text without HTML"
        names = sorted(PLACEHOLDER.findall(value))
        if placeholders is not None and names != placeholders:
            return "placeholders differ"
    return None


def references(catalog):
    paths = sorted((ROOT / "ui").glob("*.html")) + sorted((ROOT / "ui").glob("*.js"))
    paths += sorted((ROOT / "apps").glob("*/*.cs")) + sorted((ROOT / "apps/setup").glob("*.html"))
    paths += sorted((ROOT / "plugins/replaykit/src").rglob("*.cpp")) + sorted((ROOT / "plugins/replaykit/src").rglob("*.hpp"))
    paths += sorted((ROOT / "lua").rglob("*.lua"))
    used = set()
    for path in paths:
        if path.name == "helper-endpoint.js":
            continue
        rel = path.relative_to(ROOT)
        source = path.read_text(encoding="utf-8-sig")
        legacy = LEGACY_CALL.search(source)
        if legacy:
            yield f"{rel}:{source.count(chr(10), 0, legacy.start()) + 1}: text must use a message ID"
        for computed in COMPUTED_CALL.finditer(source):
            yield f"{rel}:{source.count(chr(10), 0, computed.start()) + 1}: a message ID must be written out, not computed"
        for matcher in (CALL, ATTRIBUTE):
            for match in matcher.finditer(source):
                ident = match[match.lastindex]
                used.add(ident)
                if ident not in catalog:
                    yield f"{rel}:{source.count(chr(10), 0, match.start()) + 1}: unknown message {ident}"
                elif isinstance(catalog[ident]["text"], dict) and (matcher is ATTRIBUTE or match[1] not in ("RK_M", "RK_MH", "Locales.Message", "SetupLocale.Message", "SetupLocale.Pair", "SetupLocale.Error")):
                    yield f"{rel}: plural message {ident} requires RK_M, Locales.Message or SetupLocale.Message with a numeric count"
    for ident in sorted(set(catalog) - used):
        yield f"en-US {ident}: unused message"


def compile_messages(destination=None):
    catalog = read(SOURCE / "en-US.json")
    codes = languages()
    errors = []
    if {path.stem for path in SOURCE.glob("*.json")} != set(codes):
        errors.append("message catalogs must match the supported languages")
    for ident, entry in catalog.items():
        if not ID.fullmatch(ident) or not isinstance(entry, dict) or set(entry) not in ({"text", "context"}, {"text", "context", "obsKey"}) or not isinstance(entry["context"], str) or not entry["context"].strip():
            raise ValueError(f"{ident}: expected a stable ID, text and translator context")
        if "obsKey" in entry and (not isinstance(entry["obsKey"], str) or not re.fullmatch(r"[a-zA-Z0-9_.-]+", entry["obsKey"]) or not isinstance(entry["text"], str)):
            raise ValueError(f"{ident}: invalid OBS message key")
        text = entry["text"]
        plural = isinstance(text, dict)
        issue = problem(text, plural, "en-US")
        if issue:
            raise ValueError(f"{ident}: {issue}")
        if plural and any(sorted(PLACEHOLDER.findall(value)) != sorted(PLACEHOLDER.findall(text["other"])) for value in text.values()):
            raise ValueError(f"{ident}: English plural placeholders differ")
        if plural and "{count}" not in text["other"]:
            raise ValueError(f"{ident}: plurals require {{count}}")
    result = {"en-US": {ident: entry["text"] for ident, entry in catalog.items()}}
    for code in codes:
        if code == "en-US":
            continue
        path = SOURCE / f"{code}.json"
        translated = read(path) if path.exists() else {}
        result[code] = {}
        for ident in sorted(set(translated) - set(catalog)):
            errors.append(f"{code} {ident}: unused message")
        for ident, entry in catalog.items():
            english = entry["text"]
            item = translated.get(ident)
            if not isinstance(item, dict) or set(item) != {"source", "text"} or item["source"] != revision(english):
                errors.append(f"{code} {ident}: translate or review English {english!r} (source {revision(english)}; {entry['context']})")
                continue
            plural = isinstance(english, dict)
            placeholders = sorted(PLACEHOLDER.findall(english["other"] if plural else english))
            issue = problem(item["text"], plural, code, placeholders)
            if issue:
                errors.append(f"{code} {ident}: {issue}")
                continue
            result[code][ident] = item["text"]
    errors.extend(references(catalog))
    if errors:
        raise ValueError("\n".join(errors))
    if destination is not None:
        for code, messages in result.items():
            write(destination / f"{code}.json", messages)
    print(f"Validated {len(catalog)} message IDs across {len(codes)} languages.")


def write_dock_scripts(directory):
    """the english catalog and the page runtime as scripts beside the dock pages, for a dock opened from disk before the helper answers."""
    catalog = read(SOURCE / "en-US.json")
    english = json.dumps({ident: entry["text"] for ident, entry in catalog.items()}, ensure_ascii=False, sort_keys=True)
    # escape < so no message can close the script it is loaded as
    english = english.replace("<", "\\u003c")
    runtime = re.search(r"<script>([\s\S]*?)</script>", (ROOT / "ui/_i18n.html").read_text(encoding="utf-8"))
    if runtime is None:
        raise ValueError("ui/_i18n.html holds no runtime script")
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "messages-en.js").write_text(f'window.RK_LANG="en-US";window.RK_MESSAGES={english};\n', encoding="utf-8", newline="\n")
    (directory / "i18n-runtime.js").write_text(runtime[1].strip() + "\n", encoding="utf-8", newline="\n")


def read_obs_ini(path):
    out = {}
    if not Path(path).exists():
        return out
    for line in Path(path).read_text(encoding="utf-8", errors="replace").splitlines():
        match = re.match(r'^([A-Za-z0-9_.\-]+)="(.*)"\s*$', line.strip())
        if match:
            out[match[1]] = match[2].replace('\\"', '"')
    return out


def strip_accelerator(text):
    return re.sub(r"&(?!&)", "", text).replace("&&", "&")


def seed_obs(directory):
    catalog = read(SOURCE / "en-US.json")
    for code in languages():
        if code == "en-US":
            continue
        path = SOURCE / f"{code}.json"
        messages = read(path)
        obs = read_obs_ini(str(directory / f"{code}.ini"))
        english = read_obs_ini(str(directory / "en-US.ini"))
        for ident, entry in catalog.items():
            key = entry.get("obsKey")
            if not key or ident in messages:
                continue
            if strip_accelerator(english.get(key, "")).strip() != entry["text"]:
                raise ValueError(f"{ident}: the OBS message {key} does not match the English source")
            text = strip_accelerator(obs.get(key, "")).strip()
            if not problem(text, False, code, sorted(PLACEHOLDER.findall(entry["text"]))):
                messages[ident] = {"source": revision(entry["text"]), "text": text}
        write(path, messages)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, help="write runtime catalogs into the installer staging directory")
    parser.add_argument("--seed-obs", type=Path, help="fill missing messages that declare a matching OBS key from an OBS locale folder")
    parser.add_argument("--dock", type=Path, help="write the english catalog and the page runtime as scripts into the staged dock folder")
    args = parser.parse_args()
    try:
        if args.seed_obs is not None:
            seed_obs(args.seed_obs)
        compile_messages(args.output)
        if args.dock is not None:
            write_dock_scripts(args.dock)
    except (OSError, ValueError) as error:
        parser.exit(1, f"{error}\n")
