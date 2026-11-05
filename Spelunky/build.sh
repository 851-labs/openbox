#!/bin/sh
# Builds spelunky.dll for the console (x86_64-pc-windows-msvc), on Windows or, with cargo-xwin, from macOS
# or Linux. The flags are here rather than in .cargo/config.toml, which cargo-xwin's own flags override.
#
#   Spelunky/build.sh [cargo build args]    ->  Spelunky/target/x86_64-pc-windows-msvc/release/spelunky.dll
set -eu
cd "$(dirname "$0")"
# The C runtime built in, as airplay.dll has it, so the DLL needs nothing from the package. Plain x86-64:
# the console's Jaguar cores have no AVX2 or BMI2. Delay-loaded: desktop DLLs the console may lack, which
# wgpu (window and monitor queries, driver versions) and cpal (device activation) link but don't call
# when drawing to a composition surface; and dcomp, for that surface, so a console without it says so
# rather than failing to load the DLL.
flags="-C target-feature=+crt-static -C target-cpu=x86-64"
for dll in user32 setupapi mmdevapi dcomp; do
  flags="$flags -C link-arg=/DELAYLOAD:$dll.dll"
done
flags="$flags -C link-arg=delayimp.lib"
export CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_RUSTFLAGS="$flags"
case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*) exec cargo build --release --target x86_64-pc-windows-msvc "$@" ;;
  *) exec cargo xwin build --release --target x86_64-pc-windows-msvc "$@" ;;
esac
