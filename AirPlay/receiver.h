/*
 * openbox's AirPlay receiver: UxPlay's AirPlay server (uxplay/), advertised on the network, with
 * the screen mirroring video handed over as H.264 and the audio decoded to PCM. This is what the
 * app calls (OpenBox/AirPlay/AirPlayReceiver.cs), in place of UxPlay's own uxplay.cpp.
 *
 * openbox is free software: GPL-3.0-or-later.
 */

#ifndef OPENBOX_AIRPLAY_RECEIVER_H
#define OPENBOX_AIRPLAY_RECEIVER_H

#include <stdint.h>

#ifdef _WIN32
#define AIRPLAY_API __declspec(dllexport)
#else
#define AIRPLAY_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Called on the receiver's own threads. Times are nanoseconds on one clock (this device's, as
 * near as the sender's clock can be mapped onto it), so audio and video can be lined up.
 */
typedef struct {
    /* level: 0 debug, 1 info, 2 error. */
    void (*log)(int level, const char *message);
    /* A sender was let in: the screen is about to be mirrored, or audio played. */
    void (*connected)(const char *name, const char *model);
    /* Every sender has gone. */
    void (*disconnected)(void);
    /* Mirroring started (or the sender's screen changed size): the picture's size. */
    void (*video_started)(int width, int height);
    /* One frame of H.264 (or H.265), Annex B, with SPS and PPS before key frames. */
    void (*video_frame)(const uint8_t *data, int length, int64_t time, int is_h265);
    /* Mirroring stopped; the connection may stay, for audio. */
    void (*video_stopped)(void);
    /* Decoded audio: interleaved 32-bit float samples. */
    void (*audio_frame)(const float *samples, int frames, int channels, int rate, int64_t time);
    /* The sender's volume, 0 (muted) to 1. */
    void (*volume)(float volume);
} airplay_callbacks_t;

/*
 * Starts advertising and accepting AirPlay. device_id is a MAC-style address (aa:bb:cc:dd:ee:ff)
 * that should stay the same between runs: senders remember receivers by it. width and height are
 * what the screen should be mirrored at. port is the server's (AirPlay's usual is 7000). 0 when it
 * started; negative if it couldn't.
 */
AIRPLAY_API int airplay_start(const char *name, const char *device_id, int width, int height, int fps,
                              int port, const airplay_callbacks_t *callbacks);

/* Ends any connection and stops advertising. */
AIRPLAY_API void airplay_stop(void);

#ifdef __cplusplus
}
#endif

#endif
