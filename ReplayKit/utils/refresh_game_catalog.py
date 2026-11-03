"""Refresh games.json from a downloaded Discord detectables catalog."""

import argparse
import json
from pathlib import Path
import re


SOURCE = "https://cdn.discordapp.com/detectables/games.json"
CATALOG = Path(__file__).resolve().parents[1] / "assets/obs-studio/obs-replayKit/data/games.json"


def refresh(previous, upstream):
    games = {game["id"]: game.copy() for game in previous}
    seen = set()
    for entry in upstream:
        game_id, name = entry["id"], entry["name"].strip()
        if not re.fullmatch(r"[0-9]{1,24}", game_id) or game_id in seen:
            raise ValueError(f"Invalid or duplicate game ID: {game_id}")
        seen.add(game_id)
        if not name or len(name) > 160 or any(ord(c) < 32 or 127 <= ord(c) <= 159 for c in name):
            continue
        rules = list(games.get(game_id, {}).get("executables", []))
        for exe in entry["executables"]:
            if exe["os"] != "win32" or exe.get("is_launcher"):
                continue
            rule = exe["name"].replace("\\", "/").lower().lstrip("/")
            if re.fullmatch(r'[^*?:"<>|\x00-\x1f]{1,396}\.exe', rule) and ".." not in rule and rule not in rules:
                rules.append(rule)
        game = {"id": game_id, "name": name, "executables": rules}
        icon = entry.get("icon_hash") or ""
        if re.fullmatch(r"[a-f0-9]{32}", icon):
            game["iconHash"] = icon
        steam = {sku["id"] for sku in entry["third_party_skus"] or []
                 if sku["distributor"] == "steam" and re.fullmatch(r"[1-9][0-9]{0,9}", sku["id"] or "")
                 and int(sku["id"]) <= 4294967295}
        if len(steam) == 1:
            game["steamAppId"] = int(steam.pop())
            prior = games.get(game_id, {})
            steam_icon = prior.get("steamIconHash", "")
            if prior.get("steamAppId") == game["steamAppId"] and re.fullmatch(r"[a-f0-9]{40}", steam_icon):
                game["steamIconHash"] = steam_icon
        games[game_id] = game
    return list(games.values())


def main():
    parser = argparse.ArgumentParser(description=__doc__, epilog=f"Download the input from {SOURCE}")
    parser.add_argument("source", type=Path)
    args = parser.parse_args()
    if args.source.stat().st_size > 32 * 1024 * 1024:
        raise ValueError("Source catalog is too large")
    upstream = json.loads(args.source.read_bytes())
    previous = json.loads(CATALOG.read_bytes())["games"]
    if not isinstance(upstream, list) or not len(previous) * 0.9 <= len(upstream) <= 30000:
        raise ValueError("Unexpected upstream catalog size")
    games = refresh(previous, upstream)
    rows = (json.dumps(game, ensure_ascii=False, separators=(",", ":")) for game in games)
    payload = ('{"version":2,"source":' + json.dumps(SOURCE) + ',"games":[\n'
               + ",\n".join(rows) + "\n]}\n").encode("utf-8")
    if len(games) > 30000 or len(payload) > 4 * 1024 * 1024:
        raise ValueError("Catalog exceeds the helper's limits")
    CATALOG.write_bytes(payload)
    print(f"{len(games)} games, {sum('iconHash' in g for g in games)} Discord icons, "
          f"{sum('steamAppId' in g for g in games)} Steam App IDs")


if __name__ == "__main__":
    main()
