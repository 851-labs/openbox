"""Builds the ad blocker's filters for every web app (OpenBox/AdBlock), from the lists uBlock
Origin turns on by default.

    python3 tools/build_adblock.py <output.json>

uBlock Origin matches every request against the lists' network filters and hides page elements
with their cosmetic filters. The console has no time to parse 100,000 lines of filter syntax each
time an app opens, so this does it ahead of time and keeps the parts openbox applies:

  ||ads.example^                 block a host and its subdomains        -> block
  ||ads.example^$third-party     ...when it isn't the page's own site   -> block3p
  ||example.com/ads/             block a host's path (no wildcards)     -> blockPaths
  @@||cdn.example^               never block a host                     -> allow
  @@||cdn.example^$domain=a.com  ...on a.com's pages                    -> allowOn
  @@||a.com^$generichide         no generic cosmetic filters on a.com   -> noGenericCosmetic
  @@||a.com^$elemhide/$document  no cosmetic filters on a.com           -> noCosmetic
  ##.ad-banner                   hide on every site                     -> hide
  a.com,b.com##.ad               hide on those sites                    -> hideFor
  a.com#@#.ad, ~a.com##.ad       don't hide on that site                -> unhideFor

Everything else (wildcard and regex patterns, redirects, scriptlets, procedural cosmetic filters
and HTML filtering) is left out. CI builds this into the app and publishes a fresh copy daily on
the store branch, which the app picks up by itself.
"""
import json
import re
import sys
import time
import urllib.parse
import urllib.request

LISTS = [
    ("uBlock filters", "https://ublockorigin.github.io/uAssets/filters/filters.txt"),
    ("uBlock filters - Privacy", "https://ublockorigin.github.io/uAssets/filters/privacy.txt"),
    ("uBlock filters - Unbreak", "https://ublockorigin.github.io/uAssets/filters/unbreak.txt"),
    ("uBlock filters - Quick fixes", "https://ublockorigin.github.io/uAssets/filters/quick-fixes.txt"),
    ("EasyList", "https://easylist.to/easylist/easylist.txt"),
    ("EasyPrivacy", "https://easylist.to/easylist/easyprivacy.txt"),
    ("Peter Lowe's Ad and tracking server list",
     "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=adblockplus&showintro=0&mimetype=plaintext"),
]

HOST = re.compile(r"^(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z0-9-]{2,}$")
PATH = re.compile(r"^/[A-Za-z0-9._~!$&'()+,;=:@%/?-]*$")
# Options that only narrow which requests a filter applies to: blocking or allowing the host for
# every request is close enough.
TYPE_OPTIONS = {
    "script", "image", "stylesheet", "css", "xmlhttprequest", "xhr", "subdocument", "frame", "media",
    "font", "object", "other", "ping", "websocket", "beacon", "fetch", "all", "important", "match-case",
}
GENERICHIDE = {"generichide", "ghide"}
ELEMHIDE = {"elemhide", "ehide", "document", "doc"}
# uBlock Origin's own selector extensions, which a browser's stylesheet can't use.
PROCEDURAL = re.compile(
    r":(?:has-text|-abp-[a-z-]+|style|remove|remove-attr|remove-class|upward|xpath|matches-css(?:-before|-after)?"
    r"|matches-path|matches-media|matches-attr|matches-prop|min-text-length|watch-attr|others|if|if-not|nth-ancestor"
    r"|contains)\(")


def fetch(url):
    request = urllib.request.Request(url, headers={"User-Agent": "openbox-filter-build"})
    for attempt in range(3):
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                return response.read().decode("utf-8", "replace")
        except OSError:
            if attempt == 2:
                raise
            time.sleep(5)


def lines_of(url, seen):
    """The list's lines, with its !#include files in place and !#if blocks left out."""
    if url in seen:
        return
    seen.add(url)
    depth = 0
    for line in fetch(url).splitlines():
        line = line.strip()
        if line.startswith("!#if"):
            depth += 1
        elif line.startswith("!#endif"):
            depth = max(0, depth - 1)
        elif depth:
            continue
        elif line.startswith("!#include "):
            yield from lines_of(urllib.parse.urljoin(url, line[len("!#include "):].strip()), seen)
        elif line and not line.startswith("!") and not line.startswith("["):
            yield line


class Filters:
    def __init__(self):
        self.block, self.block3p, self.allow = set(), set(), set()
        self.block_paths = {}
        self.allow_on = {}
        self.no_cosmetic, self.no_generic_cosmetic = set(), set()
        self.hide = set()
        self.hide_for, self.unhide_for = {}, {}
        self.generic_unhide = set()
        self.bad = set()
        self.used = 0

    def add(self, line):
        if line.endswith("$badfilter") or ",badfilter" in line:
            self.bad.add(re.sub(r"[$,]badfilter", "", line))
            return
        for marker in ("#@#", "##"):
            index = line.find(marker)
            if index >= 0:
                self.cosmetic(line[:index], line[index + len(marker):], marker == "#@#")
                return
        if re.search(r"#[$?%@]+#", line):
            return  # injected CSS, procedural, scriptlets: not supported
        self.network(line)

    def cosmetic(self, domains, selector, exception):
        selector = selector.strip()
        if (not selector or selector.startswith(("+js(", "^")) or PROCEDURAL.search(selector)
                or "{" in selector or "}" in selector):
            return
        positive, negative = [], []
        for domain in filter(None, domains.split(",")):
            domain = domain.strip().lower()
            target = negative if domain.startswith("~") else positive
            domain = domain.lstrip("~")
            if not HOST.match(domain):
                return  # entity (google.*) or something else this doesn't handle
            target.append(domain)
        if exception:
            if positive:
                for domain in positive:
                    self.unhide_for.setdefault(domain, set()).add(selector)
            else:
                self.generic_unhide.add(selector)
        elif positive:
            for domain in positive:
                self.hide_for.setdefault(domain, set()).add(selector)
        else:
            self.hide.add(selector)
            for domain in negative:
                self.unhide_for.setdefault(domain, set()).add(selector)
        self.used += 1

    def network(self, line):
        exception = line.startswith("@@")
        if exception:
            line = line[2:]
        pattern, _, options = line.partition("$")
        options = {o.strip().lower() for o in options.split(",") if o.strip()} if options else set()
        if not pattern.startswith("||"):
            return
        pattern = pattern[2:]
        host, slash, path = pattern.partition("/")
        if host.endswith("^"):
            host = host[:-1]
            if slash:
                return
        host = host.lower()
        if not HOST.match(host):
            return
        path = slash + path
        if path.endswith("^") or path.endswith("|"):
            path = path[:-1]
        if path and (not PATH.match(path) or "*" in path or "^" in path):
            return

        on_domains = []
        rest = set()
        for option in options:
            if option.startswith("domain=") or option.startswith("from="):
                on_domains = option.split("=", 1)[1].split("|")
            else:
                rest.add(option)
        third_party = bool(rest & {"third-party", "3p"})
        rest -= {"third-party", "3p"}

        if exception:
            if rest & GENERICHIDE and not path and not on_domains:
                self.no_generic_cosmetic.add(host)
            elif rest & ELEMHIDE and not path and not on_domains:
                self.no_cosmetic.add(host)
            elif rest <= TYPE_OPTIONS and not path:
                if not on_domains:
                    self.allow.add(host)
                else:
                    for domain in on_domains:
                        if HOST.match(domain):
                            self.allow_on.setdefault(domain, set()).add(host)
            else:
                return
        else:
            if on_domains or not rest <= TYPE_OPTIONS or rest & {"first-party", "1p"}:
                return
            if path:
                if third_party:
                    return
                self.block_paths.setdefault(host, set()).add(path)
            elif third_party:
                self.block3p.add(host)
            else:
                self.block.add(host)
        self.used += 1

    def to_json(self, lists):
        # A selector can be both hidden everywhere and excepted everywhere (#@#): it isn't hidden.
        hide = self.hide - self.generic_unhide
        # Each list is one string, a line per entry: on the console, splitting a string is much
        # quicker than reading a JSON array of 100,000 strings one by one.
        def lines(values):
            return "\n".join(sorted(values))

        def table(mapping, skip=()):
            return {key: lines(values) for key, values in sorted(mapping.items()) if key not in skip}

        return {
            "version": int(time.time()),
            "updated": time.strftime("%Y-%m-%d", time.gmtime()),
            "lists": lists,
            "block": lines(self.block - self.allow),
            "block3p": lines(self.block3p - self.block - self.allow),
            "blockPaths": table(self.block_paths, self.allow),
            "allow": lines(self.allow),
            "allowOn": table(self.allow_on),
            "noCosmetic": lines(self.no_cosmetic),
            "noGenericCosmetic": lines(self.no_generic_cosmetic),
            "hide": lines(hide),
            "hideFor": table(self.hide_for),
            "unhideFor": table(self.unhide_for),
        }


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    filters = Filters()
    lines = []
    for name, url in LISTS:
        count = 0
        for line in lines_of(url, set()):
            lines.append(line)
            count += 1
        print(f"{name}: {count} filters")
    # $badfilter turns off a filter from any list, so collect those first.
    for line in lines:
        if line.endswith("$badfilter") or ",badfilter" in line:
            filters.add(line)
    for line in lines:
        if line not in filters.bad and not (line.endswith("$badfilter") or ",badfilter" in line):
            filters.add(line)
    result = filters.to_json([name for name, _ in LISTS])
    with open(sys.argv[1], "w", encoding="utf-8") as f:
        json.dump(result, f, separators=(",", ":"), ensure_ascii=False)
    print(f"Kept {filters.used} of {len(lines)} filters: {len(filters.block)} blocked hosts, "
          f"{len(filters.block3p)} third-party, {sum(map(len, filters.block_paths.values()))} paths, "
          f"{len(filters.hide)} generic and {sum(map(len, filters.hide_for.values()))} site cosmetic filters")


if __name__ == "__main__":
    main()
