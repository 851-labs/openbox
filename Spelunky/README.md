# Spelunky

`spelunky.dll`, Spelunky Classic in OpenBox. OpenBox's Spelunky page (`OpenBox/Spelunky/`) loads it and
gives it a composition visual to show the game in.

The game is [851 Labs' port](https://github.com/851-labs/spelunky) of Spelunky Classic 1.1 to Rust, used
as it is from a release tag (`Cargo.toml`): nothing of the game is in this repository, and the port isn't
changed. This crate only hosts it, the way the port's own desktop app does (`app::window`), with the
port's pieces:

- `app::play::Play` steps the game 30 times a second, plays its sound (kira on WASAPI) and keeps its saves
  (`LocalState\Spelunky\save`).
- `app::render::Renderer` draws each frame with wgpu, on Direct3D 12, and `app::present::Presenter` puts it
  in a swap chain of the game's own size, 320x240. `src/screen.rs` shows that swap chain through a
  composition brush with nearest-neighbour sampling, scaled by the largest whole number that fits (1280x960
  on a 1080p TV) and centred, as the desktop app shows it.
- `src/pad.rs` reads the controller with Windows.Gaming.Input as GM's joystick 1, as the desktop app reads
  an XInput pad. `gamepad.cfg` starts as Spelunky HD's layout: A jumps, X whips, B bombs, Y ropes, the
  right trigger runs, LB switches items, RB buys, Menu pauses. View is OpenBox's (Home).
- `src/report.rs` sends the game's log to OpenBox's, and to `LocalState\Spelunky\spelunky.log`.

The game runs on a thread of its own. `spelunky_start` and `spelunky_resize` are called on the UI thread,
which composition needs.

## The GPU

On Xbox an app (rather than a game) has Direct3D 12 only in software (WARP); games get the GPU. Drawing
the game's 320x240 and leaving the scaling to the compositor keeps WARP at full speed: 60 frames a second
and the game's 30 ticks a second, a tick (with its drawing) in about 6 ms in the Mines. Drawing it at
1080p in software ran at 5 frames a second.

OpenBox could run as a game instead (the `expandedResources` capability), which gives it the GPU, but then
the console won't start OpenBox without someone signed in, and the whole launcher runs under a game's
resource rules. The same code uses the GPU wherever there is one.

## The game's files

Spelunky's sprites, sounds and rooms aren't in OpenBox, its repository or its packages. The first time the
game opens, the page downloads them from the port's own GitHub release, the one the DLL is built from
(`spelunky-<version>-windows-x64.zip`), checks it against the release's SHA-256, and keeps its `assets/`,
`LICENSE` and `NOTICE.txt` (`OpenBox/Spelunky/SpelunkyAssets.cs`). Moving to a new release means changing
the tag in `Cargo.toml` and the version and SHA-256 in `SpelunkyAssets.cs` together.

## Building

CI builds it on Windows with the app (`.github/workflows/app.yml`), with `build.sh`. From a Mac:

```sh
cargo install cargo-xwin
Spelunky/build.sh    # -> Spelunky/target/x86_64-pc-windows-msvc/release/spelunky.dll
```

A Debug build of OpenBox uses a `spelunky.dll` in `LocalState\Spelunky\dev` instead of its own, if there is
one: upload it with Device Portal's file explorer and restart OpenBox to try a change without building the
app.

## License

Spelunky is Copyright (c) 2008, 2009 Derek Yu and Mossmouth, LLC, and the port is distributed under the
Spelunky User License v1.1b: it may not be sold, and copies keep the license and say they're an unofficial
mod. `spelunky.dll` is built from the port's source and goes out with OpenBox's packages under that license,
not OpenBox's GPL-3.0; the page says it's an unofficial port before it starts, the game shows its own notice
when it does, and the release's `LICENSE` and `NOTICE.txt` are kept beside its files.
