# Store apps

Each app in openbox's Store is a JSON file in this folder, with its icon in `icons/`. To add one, open a pull request with both files. CI checks them, and once the pull request is merged the app appears in the Store on every console.

`apps/my-app.json`:

```json
{
  "name": "My App",
  "description": "One line about it.",
  "url": "https://example.com",
  "icon": "icons/my-app.png",
  "color": "#1A2B3C"
}
```

- The file name is the app's id: lowercase letters, digits and dashes.
- `name`: up to 24 characters, shown under the tile.
- `description`: up to 80 characters, shown in the Store.
- `url`: where the app opens (https only).
- `icon`: a 256x256 PNG in `icons/`, at most 200 KB. A glyph on a transparent background looks best; it's drawn in the middle of the tile.
- `color`: the tile's background.
- `input` (optional): how the controller drives the page.
  - `"pointer"` (the default): the console's mouse pointer, as in a browser.
  - `"remote"`: for web apps made for TVs. The D-pad, A and B become arrow keys, Enter and Escape. Twitch uses it: `olivine.tv.twitch.tv` is the TV app Twitch made for Xbox.
- `userAgent` (optional): for TV web apps that only serve their TV version to TVs.

In every app, View goes back to the homescreen. B goes back a page (with `"remote"`, it's the app's own back key, and the app going home when it closes itself).

Every app gets openbox's ad blocker; there's nothing to set.

## Package apps

An app can also be an app package of its own (an `.appx` or `.msix`) instead of a web page. Installing it from the Store downloads the package, installs it on the console, and adds a tile that opens it. Instead of `url`, `input` and `userAgent` it has a `package`:

```json
{
  "name": "Minecraft Java",
  "description": "Minecraft: Java Edition, through Bandit Launcher. Needs a Java Edition account.",
  "icon": "icons/minecraft-java.png",
  "color": "#107C10",
  "credit": "Bandit Launcher by veroxsity / BanditVault: launcher.banditvault.co.uk",
  "package": {
    "url": "https://github.com/veroxsity/JavaUWP/releases/download/nightly/BanditLauncher-nightly.appx",
    "sha256Url": "https://github.com/veroxsity/JavaUWP/releases/download/nightly/BanditLauncher-nightly.sha256",
    "familyName": "BanditVault.Launcher_r9d4qta1rpjdr",
    "appId": "App"
  }
}
```

- `package.url`: the package, where its developer publishes it. openbox downloads it from there and never hosts a copy, so link the official download.
- `package.sha256Url` (optional): a file whose first word is the package's SHA-256, published with it. The download is checked against it.
- `package.familyName`: the installed package's family name, from its manifest's Identity (`Name_PublisherId`).
- `package.appId` (optional, `App` by default): the Application Id the tile opens.
- `credit` (optional, any app): who made it, with a link, up to 100 characters. It's shown in the Store, so use it when an app's terms ask for credit.

The package's dependencies must already be on the console. Apps built like openbox need `Microsoft.VCLibs.140.00`, which openbox itself brings.

To check your files before opening the pull request, run `python3 tools/build_store.py`.
