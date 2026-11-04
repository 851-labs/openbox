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
RESERVED = {"youtube", "x", "store", "airplay", "openbox"}
# Every app has these, and either a web app's url or a package app's package.
REQUIRED = {"name", "description", "icon", "color"}
WEB_FIELDS = {"url", "input", "userAgent"}
OPTIONAL = WEB_FIELDS | {"package", "credit"}
PACKAGE_FIELDS = {"url", "sha256Url", "familyName", "appId"}
HTTPS = re.compile(r"^https://[^\s/]+\.[^\s/]+(/\S*)?$")
# Name_PublisherId, as Windows writes package family names.
FAMILY_NAME = re.compile(r"^[A-Za-z0-9.-]{3,50}_[0-9a-hjkmnp-tv-z]{13}$")
APP_ID = re.compile(r"^[A-Za-z][A-Za-z0-9.]{0,63}$")
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
        error(f'"{app_id}" is built into OpenBox')
    try:
        with open(os.path.join(APPS, file_name), encoding="utf-8") as f:
            app = json.load(f)
    except (OSError, ValueError) as e:
        error(f"isn't valid JSON ({e})")
        return None, None, errors
    if not isinstance(app, dict):
        error("must be a JSON object")
        return None, None, errors

    package = app.get("package")
    for key in sorted(REQUIRED - app.keys()):
        error(f'"{key}" is missing')
    for key in sorted(app.keys() - REQUIRED - OPTIONAL):
        error(f'unknown field "{key}"')
    if package is None and "url" not in app:
        error('"url" is missing (or "package", for an app package)')
    if package is not None and app.keys() & WEB_FIELDS:
        error('a package app has "package" instead of ' + ", ".join(f'"{k}"' for k in sorted(app.keys() & WEB_FIELDS)))
    for key, value in app.items():
        if key != "package" and (not isinstance(value, str) or not value.strip()):
            error(f'"{key}" must be a non-empty string')
    if package is not None:
        errors += check_package(package, error)
    if errors:
        return None, None, errors

    if len(app["name"]) > 24:
        error('"name" is longer than 24 characters')
    if len(app["description"]) > 80:
        error('"description" is longer than 80 characters')
    if "url" in app and not HTTPS.match(app["url"]):
        error('"url" must be an https:// address')
    if len(app.get("credit", "")) > 100:
        error('"credit" is longer than 100 characters')
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
        # The hash changes the address when the icon does, so the console doesn't keep the old one.
        "icon": f"{BASE_URL}/icons/{app_id}.png?v={version}",
        "color": app["color"].upper(),
    }
    if package is not None:
        entry["package"] = {"appId": "App", **package}
    else:
        entry["url"] = app["url"]
        entry["input"] = app.get("input", "pointer")
    for key in ("userAgent", "credit"):
        if key in app:
            entry[key] = app[key]
    return entry, icon, []


def check_package(package, error):
    """A package app's "package": where its developer publishes it, and what it installs as."""
    if not isinstance(package, dict):
        error('"package" must be an object')
        return []
    for key in sorted({"url", "familyName"} - package.keys()):
        error(f'"package.{key}" is missing')
    for key in sorted(package.keys() - PACKAGE_FIELDS):
        error(f'unknown field "package.{key}"')
    for key, value in package.items():
        if not isinstance(value, str) or not value.strip():
            error(f'"package.{key}" must be a non-empty string')
            return []
    for key in ("url", "sha256Url"):
        if key in package and not HTTPS.match(package[key]):
            error(f'"package.{key}" must be an https:// address')
    if "url" in package and not re.search(r"\.(appx|msix)$", package["url"], re.I):
        error('"package.url" must be an .appx or .msix')
    if "familyName" in package and not FAMILY_NAME.match(package["familyName"]):
        error('"package.familyName" must be a package family name, e.g. Contoso.App_8wekyb3d8bbwe')
    if "appId" in package and not APP_ID.match(package["appId"]):
        error('"package.appId" must be an Application Id from the package\'s manifest')
    return []


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
