# openbox

A simple launcher for Xbox consoles in Developer Mode. It boots to a homescreen of apps you navigate with the controller, comes with YouTube built in (AirPlay is next), and has a small app store where apps are added by pull request.

## Using it
- **Homescreen:** move between tiles with the D-pad or left stick, press **A** to open one. Press **X** on a tile for its options: **Move** picks it up so the D-pad moves it (A to put it down, B to put it back), and **Uninstall** removes a store app.
- **Anywhere:** **View** (the two overlapping squares) goes back to the homescreen.
- **In an app:** **B** belongs to the app: back a page, or in TV apps like YouTube and Twitch their own back. Backing out of YouTube's start screen also goes home.
- **Store:** the last tile. Press **A** on an app to add it to the homescreen, or on an added app to remove it.

## Apps
- **YouTube** is YouTube's own TV app (youtube.com/tv), with its ads removed. That part comes from [TubeBox](https://github.com/sharktie/tubebox-uwp).
- **Store apps** are web apps that open full screen. Each one is a JSON file in [`apps/`](apps/); see [apps/README.md](apps/README.md) to add one. Twitch opens Twitch's own TV app.
- **Ad blocking** in every web app, the way uBlock Origin does it, with its default lists (uBlock filters, EasyList, EasyPrivacy, Peter Lowe's list): requests to ad and tracking servers are blocked, and leftover ad slots on the page are hidden. The lists update by themselves daily.

## Install on Xbox
1. Turn on [Developer Mode](https://learn.microsoft.com/windows/uwp/xbox-apps/devkit-activation) and, in Dev Home, the Xbox Device Portal.
2. Download the `openbox-x64` artifact from the latest successful [App workflow run](../../actions/workflows/app.yml) and unzip it. It contains `openbox_…_x64.msix` and a `Dependencies` folder.
3. In Device Portal, under **My games & apps**, choose **Add**: pick the `.msix`, then add every file from `Dependencies`, then **Start**.
4. Open **openbox** from Dev Home.

Installing a newer build over an older one keeps your added apps and sign-ins.

## How it's built
A C# UWP app with XAML (`OpenBox/`):

- `Home/`: the homescreen.
- `Web/WebAppPage`: any web app, full screen in WebView2. The controller drives it as the console's mouse pointer, or as a TV remote for TV web apps (`Web/Inject/remote.js`).
- `AdBlock/`: the ad blocker for web apps. `tools/build_adblock.py` compiles the filter lists into what it reads.
- `YouTube/`: what YouTube adds to `WebAppPage`: a TV user agent and TubeBox's YouTube ad blocking, in place of the general one.
- `Store/`: the Store page, reading the index CI builds from `apps/`.
- `Apps/BuiltInApps.cs`: the built-in apps. A built-in native app (AirPlay, next) is a XAML page registered there.

CI (`.github/workflows/`) builds a signed `.msix` on a Windows runner (`app.yml`). It also checks `apps/` and, on main and daily, publishes the store index, icons and ad filters to the `store` branch (`store.yml`). `tools/xbox.sh` installs a build on a console through Device Portal.

## License
GPL-3.0. The ad blocker's filters are compiled at build time from uBlock Origin's lists (GPL-3.0), EasyList and EasyPrivacy (GPL-3.0 or CC BY-SA 3.0) and Peter Lowe's list, under their own licenses. Parts of openbox (the YouTube scripts in `OpenBox/YouTube/Inject`, the ad filter and controller code, and the build setup) come from [TubeBox](https://github.com/sharktie/tubebox-uwp) by sharktie, also GPL-3.0.

openbox is not affiliated with Microsoft, YouTube or Google.
