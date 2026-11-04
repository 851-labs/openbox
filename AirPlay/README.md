# AirPlay receiver

`airplay.dll`, which makes the console an AirPlay receiver for screen mirroring and audio. openbox
loads it from `OpenBox/AirPlay/AirPlayReceiver.cs`; `receiver.h` is its interface.

- `uxplay/`: the AirPlay server from [UxPlay](https://github.com/FDH2/UxPlay) (`lib/`, at commit
  `b1be2bd`, November 2026), unchanged: pairing, FairPlay, the RTSP server, the mirroring and audio
  streams, and its own mDNS responder (`mdnsd/`) to advertise the receiver. Its `crypto.c` and
  `srp.c` are left out, replaced by `port/`.
- `receiver.c`: what UxPlay's `uxplay.cpp` does, for openbox. It starts the server, passes the
  mirrored screen's H.264 on, and decodes the audio (AAC-ELD when mirroring, ALAC or AAC for audio
  alone) with FFmpeg's decoders. GStreamer isn't used: the app decodes the video in hardware.
- `port/`: what UxPlay gets from libraries that aren't on the console. `crypto.c` does UxPlay's
  `crypto.h` with [Monocypher](https://monocypher.org) (`monocypher/`, 4.0.2) for Ed25519, X25519
  and SHA-512, and the system's AES; `plist.c` is the part of libplist UxPlay uses; `srp.c` stands
  in for PIN pairing, which openbox doesn't offer; `win32/` has the POSIX threads and calls UxPlay
  expects.

## Building

CI builds it on Windows with the app (`.github/workflows/app.yml`), after building FFmpeg's AAC and
ALAC decoders with `tools/build_ffmpeg.sh`. It's a plain Win32 DLL with the C runtime built in.

On a Mac, the same code builds into `airplay-test` (`test/main.c`), which advertises a receiver and
saves what it's sent, for trying a change against a real sender without the console:

```sh
brew install cmake ninja ffmpeg pkg-config
cmake -S AirPlay -B build/airplay -G Ninja && cmake --build build/airplay
AIRPLAY_PORT=7100 build/airplay/airplay-test    # macOS's own receiver has port 7000
# Mirror to "openbox test" (System Settings > Displays > + > Mirror or extend to), then:
ffplay video.h264
ffplay -f f32le -ar 44100 -ch_layout stereo audio.f32
```

## Licenses

UxPlay is GPL-3.0 (its library files LGPL-2.1-or-later, llhttp MIT, playfair GPL-3.0); Monocypher
is BSD-2-Clause or CC0; FFmpeg, as built here, LGPL-2.1-or-later. openbox is GPL-3.0.
