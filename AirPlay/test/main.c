/*
 * Runs the receiver on a computer: it shows up as an AirPlay receiver, and what's sent to it
 * goes to video.h264 and audio.f32 (32-bit float, stereo, 44.1 kHz) in the current folder.
 *
 *   airplay-test [name]
 *   ffplay video.h264; ffplay -f f32le -ar 44100 -ch_layout stereo audio.f32
 *
 * openbox is free software: GPL-3.0-or-later.
 */

#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include "../receiver.h"

static FILE *video;
static FILE *audio;
static long frames, samples;
static volatile int stopping;

static void on_log(int level, const char *message) {
    static const char *levels[] = { "debug", "info", "error" };
    if (level > 0 || getenv("AIRPLAY_DEBUG")) {
        fprintf(stderr, "[%s] %s\n", levels[level], message);
    }
}

static void on_connected(const char *name, const char *model) { fprintf(stderr, "== connected: %s (%s)\n", name, model); }
static void on_disconnected(void) { fprintf(stderr, "== disconnected\n"); }
static void on_video_started(int width, int height) { fprintf(stderr, "== video %dx%d\n", width, height); }
static void on_video_stopped(void) { fprintf(stderr, "== video stopped\n"); }
static void on_volume(float decibels) { fprintf(stderr, "== volume %.1f dB\n", decibels); }

static void on_video_frame(const uint8_t *data, int length, int64_t time, int is_h265) {
    (void) is_h265;
    fwrite(data, 1, length, video);
    if (++frames % 60 == 0) {
        fprintf(stderr, "== %ld frames, last %d bytes at %.3f s\n", frames, length, time / 1e9);
    }
}

static void on_audio_frame(const float *data, int count, int channels, int rate, int64_t time) {
    fwrite(data, sizeof(float), (size_t) count * channels, audio);
    samples += count;
    if (samples / rate != (samples - count) / rate) {
        fprintf(stderr, "== %ld s of audio (%d ch, %d Hz) at %.3f s\n", samples / rate, channels, rate, time / 1e9);
    }
}

static void on_signal(int sig) { (void) sig; stopping = 1; }

int main(int argc, char **argv) {
    const char *name = argc > 1 ? argv[1] : "openbox test";
    airplay_callbacks_t callbacks = {
        on_log, on_connected, on_disconnected, on_video_started, on_video_frame, on_video_stopped,
        on_audio_frame, on_volume,
    };
    video = fopen("video.h264", "wb");
    audio = fopen("audio.f32", "wb");
    signal(SIGINT, on_signal);
    signal(SIGTERM, on_signal);
    if (airplay_start(name, getenv("AIRPLAY_ID") ? getenv("AIRPLAY_ID") : "02:0b:0c:0d:0e:0f", 1920, 1080, 60,
                      getenv("AIRPLAY_PORT") ? atoi(getenv("AIRPLAY_PORT")) : 7000, &callbacks)) {
        return 1;
    }
    while (!stopping) {
        sleep(1);
    }
    airplay_stop();
    fclose(video);
    fclose(audio);
    return 0;
}
