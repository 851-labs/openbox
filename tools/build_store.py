"""Checks the store's apps (apps/*.json) and builds the index the app reads.

    python3 tools/build_store.py           # check only
    python3 tools/build_store.py _site     # check, then write _site/index.json and _site/icons/

CI runs it on every pull request, and on main publishes the result on the repository's store
branch (https://raw.githubusercontent.com/851-labs/openbox/store/index.json). See apps/README.md
for the format.
"""
import hashlib
import json
import os
import re
import shutil
import struct
import sys
from datetime import datetime, timezone

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
APPS = os.path.join(ROOT, "apps")
BASE_URL = os.environ.get("STORE_BASE_URL", "https://raw.githubusercontent.com/851-labs/openbox/store")

ID = re.compile(r"^[a-z0-9]+(-[a-z0-9]+)*$")
COLOR = re.compile(r"^#[0-9A-Fa-f]{6}$")
# Built into openbox; a store app can't take their place.
RESERVED = {"youtube", "store", "airplay", "openbox"}
REQUIRED = {"name", "description", "url", "icon", "color"}
OPTIONAL = {"input", "userAgent"}
MAX_ICON_BYTES = 200 * 1024


def png_size(path):
    with open(path, "rb") as f:
        header = f.read(24)
    if len(header) < 24 or header[:8] != b"\x89PNG\r\n\x1a\n" or header[12:16] != b"IHDR":
        return None
    return struct.unpack(">II", header[16:24])


def check_app(file_name):
    """Returns (entry for the index, icon path, errors)."""
    app_id = file_name[:-len(".json")]
    errors = []

    def error(message):
        errors.append(f"apps/{file_name}: {message}")

    if not ID.match(app_id):
        error("the file name is the app's id: lowercase letters, digits and dashes only")
    if app_id in RESERVED:
        error(f'"{app_id}" is built into openbox')
    try:
        with open(os.path.join(APPS, file_name), encoding="utf-8") as f:
            app = json.load(f)
    except (OSError, ValueError) as e:
        error(f"isn't valid JSON ({e})")
        return None, None, errors
    if not isinstance(app, dict):
        error("must be a JSON object")
        return None, None, errors

    for key in sorted(REQUIRED - app.keys()):
        error(f'"{key}" is missing')
    for key in sorted(app.keys() - REQUIRED - OPTIONAL):
        error(f'unknown field "{key}"')
    for key, value in app.items():
        if not isinstance(value, str) or not value.strip():
            error(f'"{key}" must be a non-empty string')
    if errors:
        return None, None, errors

    if len(app["name"]) > 24:
        error('"name" is longer than 24 characters')
    if len(app["description"]) > 80:
        error('"description" is longer than 80 characters')
    if not re.match(r"^https://[^\s/]+\.[^\s/]+(/\S*)?$", app["url"]):
        error('"url" must be an https:// address')
    if not COLOR.match(app["color"]):
        error('"color" must look like #1A2B3C')
    if app.get("input", "pointer") not in ("pointer", "remote"):
        error('"input" must be "pointer" or "remote"')
    if "userAgent" in app and len(app["userAgent"]) > 500:
        error('"userAgent" is longer than 500 characters')

    icon = os.path.normpath(os.path.join(APPS, app["icon"]))
    if not icon.startswith(os.path.join(APPS, "icons") + os.sep) or not icon.endswith(".png"):
        error('"icon" must be a .png in apps/icons/, e.g. "icons/my-app.png"')
    elif not os.path.isfile(icon):
        error(f'"icon": {app["icon"]} doesn\'t exist')
    else:
        size = png_size(icon)
        if size is None:
            error(f'"icon": {app["icon"]} isn\'t a PNG')
        elif size != (256, 256):
            error(f'"icon": {app["icon"]} is {size[0]}x{size[1]}; it must be 256x256')
        if os.path.getsize(icon) > MAX_ICON_BYTES:
            error(f'"icon": {app["icon"]} is over {MAX_ICON_BYTES // 1024} KB')
    if errors:
        return None, None, errors

    with open(icon, "rb") as f:
        version = hashlib.sha256(f.read()).hexdigest()[:8]
    entry = {
        "id": app_id,
        "name": app["name"],
        "description": app["description"],
        "url": app["url"],
        # The hash changes the address when the icon does, so the console doesn't keep the old one.
        "icon": f"{BASE_URL}/icons/{app_id}.png?v={version}",
        "color": app["color"].upper(),
        "input": app.get("input", "pointer"),
    }
    if "userAgent" in app:
        entry["userAgent"] = app["userAgent"]
    return entry, icon, []


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else None
    entries, icons, errors = [], {}, []
    for file_name in sorted(os.listdir(APPS)):
        if not file_name.endswith(".json"):
            continue
        entry, icon, app_errors = check_app(file_name)
        errors += app_errors
        if entry:
            entries.append(entry)
            icons[entry["id"]] = icon
    if errors:
        for message in errors:
            print(f"::error::{message}" if os.environ.get("GITHUB_ACTIONS") else message, file=sys.stderr)
        sys.exit(1)
    entries.sort(key=lambda e: e["name"].lower())
    print(f"{len(entries)} apps OK: " + ", ".join(e["id"] for e in entries))
    if not out:
        return

    os.makedirs(os.path.join(out, "icons"), exist_ok=True)
    for app_id, icon in icons.items():
        shutil.copyfile(icon, os.path.join(out, "icons", f"{app_id}.png"))
    index = {
        "version": 1,
        "generated": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "apps": entries,
    }
    with open(os.path.join(out, "index.json"), "w", encoding="utf-8") as f:
        json.dump(index, f, indent=2, ensure_ascii=False)
        f.write("\n")
    print(f"Wrote {out}/index.json")


if __name__ == "__main__":
    main()
