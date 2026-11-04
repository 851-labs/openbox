/*
 * openbox's AirPlay receiver (see receiver.h): what uxplay.cpp does for UxPlay, without
 * GStreamer. It advertises the receiver (UxPlay's mdnsd), runs UxPlay's server, passes the
 * mirrored screen's H.264 through, and decodes the audio (AAC-ELD when mirroring, ALAC or AAC
 * otherwise) with FFmpeg's decoders.
 *
 * openbox is free software: GPL-3.0-or-later.
 */

#include <stdarg.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "receiver.h"
#include "uxplay/compat.h"
#include "uxplay/dnssd.h"
#include "uxplay/raop.h"
#include "uxplay/raop_ntp.h"
#include "uxplay/stream.h"
#include "uxplay/logger.h"

#ifdef OPENBOX_HAVE_FFMPEG
#include <libavcodec/avcodec.h>
#include <libavutil/channel_layout.h>
#endif

#define SECOND_NS 1000000000LL
/* Senders ask for feedback every 2 seconds; this long without it, they're gone. */
#define MISSED_FEEDBACK_LIMIT 15

static airplay_callbacks_t callbacks;
static dnssd_t *dnssd;
static raop_t *raop;

static mutex_handle_t lock;
static bool lock_ready;
static int open_connections;
static int missed_feedback;
static bool reset_requested;
static int64_t remote_clock_offset;

static thread_handle_t watchdog;
static bool watchdog_running;
static cond_handle_t watchdog_wake;

static void log_line(int level, const char *format, ...) {
    char message[1024];
    va_list args;
    va_start(args, format);
    vsnprintf(message, sizeof(message), format, args);
    va_end(args);
    if (callbacks.log) {
        callbacks.log(level, message);
    }
}

/* Audio. */

#ifdef OPENBOX_HAVE_FFMPEG
static AVCodecContext *decoder;
static AVPacket *packet;
static AVFrame *frame;
static unsigned char decoder_type;
static float *interleaved;
static int interleaved_capacity;

/* The decoders' setup, as UxPlay gives GStreamer (renderers/audio_renderer.c). */
static const uint8_t alac_cookie[] = {
    0x00, 0x00, 0x00, 0x24, 0x61, 0x6c, 0x61, 0x63, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x01, 0x60, 0x00, 0x10, 0x28, 0x0a, 0x0e, 0x02, 0x00, 0xff,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xac, 0x44,
};
static const uint8_t aac_lc_config[] = { 0x12, 0x10 };
static const uint8_t aac_eld_config[] = { 0xf8, 0xe8, 0x50, 0x00 };

static void audio_decoder_close(void) {
    avcodec_free_context(&decoder);
    av_packet_free(&packet);
    av_frame_free(&frame);
    decoder_type = 0;
}

/* ct: 2 ALAC, 4 AAC-LC, 8 AAC-ELD (all 44.1 kHz stereo). */
static void audio_decoder_open(unsigned char ct) {
    const uint8_t *config;
    size_t config_size;
    enum AVCodecID codec_id = AV_CODEC_ID_AAC;
    audio_decoder_close();
    switch (ct) {
    case 2:
        codec_id = AV_CODEC_ID_ALAC;
        config = alac_cookie;
        config_size = sizeof(alac_cookie);
        break;
    case 4:
        config = aac_lc_config;
        config_size = sizeof(aac_lc_config);
        break;
    case 8:
        config = aac_eld_config;
        config_size = sizeof(aac_eld_config);
        break;
    default:
        log_line(2, "Audio format %d isn't supported", ct);
        return;
    }
    const AVCodec *codec = avcodec_find_decoder(codec_id);
    decoder = codec ? avcodec_alloc_context3(codec) : NULL;
    if (!decoder) {
        log_line(2, "No decoder for audio format %d", ct);
        return;
    }
    decoder->extradata = (uint8_t *) av_mallocz(config_size + AV_INPUT_BUFFER_PADDING_SIZE);
    memcpy(decoder->extradata, config, config_size);
    decoder->extradata_size = (int) config_size;
    decoder->sample_rate = 44100;
    av_channel_layout_default(&decoder->ch_layout, 2);
    if (avcodec_open2(decoder, codec, NULL) < 0) {
        log_line(2, "The audio decoder for format %d didn't open", ct);
        avcodec_free_context(&decoder);
        return;
    }
    packet = av_packet_alloc();
    frame = av_frame_alloc();
    decoder_type = ct;
    log_line(1, "Audio: %s", ct == 2 ? "ALAC" : ct == 4 ? "AAC-LC" : "AAC-ELD");
}

static void audio_decode(const unsigned char *data, int length, int64_t time) {
    if (!decoder || av_new_packet(packet, length) < 0) {
        return;
    }
    memcpy(packet->data, data, length);
    int sent = avcodec_send_packet(decoder, packet);
    av_packet_unref(packet);
    if (sent < 0) {
        return;
    }
    while (avcodec_receive_frame(decoder, frame) == 0) {
        int channels = frame->ch_layout.nb_channels;
        int frames = frame->nb_samples;
        if (channels < 1 || frames < 1) {
            continue;
        }
        if (interleaved_capacity < frames * channels) {
            interleaved_capacity = frames * channels;
            interleaved = (float *) realloc(interleaved, interleaved_capacity * sizeof(float));
        }
        for (int c = 0; c < channels; c++) {
            for (int i = 0; i < frames; i++) {
                float sample;
                switch (frame->format) {
                case AV_SAMPLE_FMT_FLTP: sample = ((const float *) frame->data[c])[i]; break;
                case AV_SAMPLE_FMT_FLT: sample = ((const float *) frame->data[0])[i * channels + c]; break;
                case AV_SAMPLE_FMT_S16P: sample = ((const int16_t *) frame->data[c])[i] / 32768.0f; break;
                case AV_SAMPLE_FMT_S16: sample = ((const int16_t *) frame->data[0])[i * channels + c] / 32768.0f; break;
                case AV_SAMPLE_FMT_S32P: sample = ((const int32_t *) frame->data[c])[i] / 2147483648.0f; break;
                case AV_SAMPLE_FMT_S32: sample = ((const int32_t *) frame->data[0])[i * channels + c] / 2147483648.0f; break;
                default: sample = 0; break;
                }
                interleaved[i * channels + c] = sample;
            }
        }
        if (callbacks.audio_frame) {
            callbacks.audio_frame(interleaved, frames, channels, frame->sample_rate, time);
        }
    }
}
#else
static void audio_decoder_open(unsigned char ct) { log_line(1, "Audio format %d: no decoder in this build", ct); }
static void audio_decoder_close(void) {}
static void audio_decode(const unsigned char *data, int length, int64_t time) { (void) data; (void) length; (void) time; }
#endif

/* Maps the sender's clock onto ours: fixed when the first audio or video arrives. */
static int64_t local_time_of(uint64_t ntp_time_local, uint64_t ntp_time_remote) {
    MUTEX_LOCK(lock);
    if (!remote_clock_offset) {
        int64_t local = (int64_t) (ntp_time_local ? ntp_time_local : raop_ntp_get_local_time());
        remote_clock_offset = local - (int64_t) ntp_time_remote;
    }
    int64_t offset = remote_clock_offset;
    MUTEX_UNLOCK(lock);
    return (int64_t) ntp_time_remote + offset;
}

/* UxPlay's callbacks. */

static void on_audio_process(void *cls, raop_ntp_t *ntp, audio_decode_struct *data) {
    (void) cls; (void) ntp;
    audio_decode(data->data, data->data_len, local_time_of(data->ntp_time_local, data->ntp_time_remote));
}

static void on_video_process(void *cls, raop_ntp_t *ntp, video_decode_struct *data) {
    (void) cls; (void) ntp;
    if (callbacks.video_frame) {
        callbacks.video_frame(data->data, data->data_len,
                              local_time_of(data->ntp_time_local, data->ntp_time_remote), data->is_h265);
    }
}

static void on_audio_get_format(void *cls, unsigned char *ct, unsigned short *spf, bool *using_screen,
                                bool *is_media, uint64_t *audio_format) {
    (void) cls;
    log_line(0, "Audio format: ct=%d spf=%d usingScreen=%d isMedia=%d audioFormat=0x%llx",
             *ct, *spf, *using_screen, *is_media, (unsigned long long) *audio_format);
    audio_decoder_open(*ct);
}

static int on_video_set_codec_and_size(void *cls, video_codec_t codec, unsigned short width_source,
                                       unsigned short height_source, unsigned short x_offset,
                                       unsigned short y_offset, unsigned short width, unsigned short height) {
    (void) cls; (void) x_offset; (void) y_offset;
    log_line(1, "Mirroring %s, %dx%d (source %dx%d)", codec == VIDEO_CODEC_H265 ? "H.265" : "H.264",
             width, height, width_source, height_source);
    if (callbacks.video_started) {
        callbacks.video_started(width, height);
    }
    return 0;
}

static void on_video_reset(void *cls, reset_type_t type) {
    (void) cls;
    log_line(0, "Video reset (%d)", (int) type);
    MUTEX_LOCK(lock);
    remote_clock_offset = 0;
    MUTEX_UNLOCK(lock);
    if (callbacks.video_stopped) {
        callbacks.video_stopped();
    }
}

static void on_conn_init(void *cls) {
    (void) cls;
    MUTEX_LOCK(lock);
    open_connections++;
    missed_feedback = 0;
    MUTEX_UNLOCK(lock);
}

static void on_conn_destroy(void *cls) {
    (void) cls;
    MUTEX_LOCK(lock);
    bool last = --open_connections <= 0;
    if (last) {
        open_connections = 0;
        remote_clock_offset = 0;
    }
    MUTEX_UNLOCK(lock);
    if (last) {
        audio_decoder_close();
        if (callbacks.disconnected) {
            callbacks.disconnected();
        }
    }
}

static void on_conn_feedback(void *cls) {
    (void) cls;
    MUTEX_LOCK(lock);
    missed_feedback = 0;
    MUTEX_UNLOCK(lock);
}

static void on_conn_reset(void *cls, int reason) {
    (void) cls;
    log_line(1, "Connection lost (%d)", reason);
    MUTEX_LOCK(lock);
    reset_requested = true;
    COND_SIGNAL(watchdog_wake);
    MUTEX_UNLOCK(lock);
}

static void on_report_client_request(void *cls, char *deviceid, char *model, char *name, bool *admit) {
    (void) cls;
    log_line(1, "Connection from %s (%s)", name ? name : "?", model ? model : "?");
    (void) deviceid;
    *admit = true;
    if (callbacks.connected) {
        callbacks.connected(name ? name : "", model ? model : "");
    }
}

static double on_audio_set_client_volume(void *cls) {
    (void) cls;
    return 0.0;
}

/* AirPlay volume is -30 dB (quietest) to 0 dB, and -144 for muted. */
static void on_audio_set_volume(void *cls, float volume) {
    (void) cls;
    float level = volume <= -144.0f ? 0.0f : volume >= 0.0f ? 1.0f : volume < -30.0f ? 0.0f : (30.0f + volume) / 30.0f;
    log_line(1, "Volume %.1f dB", volume);
    if (callbacks.volume) {
        callbacks.volume(level);
    }
}

static const char *on_passwd(void *cls, int *len) {
    (void) cls;
    *len = 0;
    return NULL;
}

static bool on_check_register(void *cls, const char *pk) {
    (void) cls; (void) pk;
    return true;
}

static void on_log(void *cls, int level, const char *msg) {
    (void) cls;
    if (!callbacks.log) {
        return;
    }
    /* UxPlay ends many lines with a newline of its own. */
    char line[2048];
    snprintf(line, sizeof(line), "%s", msg);
    size_t n = strlen(line);
    while (n && (line[n - 1] == '\n' || line[n - 1] == '\r')) {
        line[--n] = '\0';
    }
    callbacks.log(level <= LOGGER_ERR ? 2 : level <= LOGGER_INFO ? 1 : 0, line);
}

/*
 * Restarts the server when a connection is lost (UxPlay's main loop does the same), and notices
 * senders that stopped asking for feedback without saying goodbye. It can't be done on UxPlay's
 * own threads, which the restart stops.
 */
static THREAD_RETVAL watchdog_main(void *arg) {
    (void) arg;
    MUTEX_LOCK(lock);
    while (watchdog_running) {
        struct timespec until;
        clock_gettime(CLOCK_REALTIME, &until);
        until.tv_sec += 1;
        pthread_cond_timedwait(&watchdog_wake, &lock, &until);
        if (!watchdog_running) {
            break;
        }
        if (open_connections && ++missed_feedback > MISSED_FEEDBACK_LIMIT) {
            log_line(1, "No feedback from the sender for %d seconds: dropping it", MISSED_FEEDBACK_LIMIT);
            reset_requested = true;
        }
        if (reset_requested) {
            reset_requested = false;
            missed_feedback = 0;
            MUTEX_UNLOCK(lock);
            raop_stop_httpd(raop);
            raop_remove_known_connections(raop);
            unsigned short port = raop_get_port(raop);
            raop_start_httpd(raop, &port);
            raop_set_port(raop, port);
            MUTEX_LOCK(lock);
        }
    }
    MUTEX_UNLOCK(lock);
    return 0;
}

static int parse_device_id(const char *device_id, char hw_addr[6]) {
    unsigned int bytes[6];
    if (!device_id || sscanf(device_id, "%2x:%2x:%2x:%2x:%2x:%2x", &bytes[0], &bytes[1], &bytes[2],
                             &bytes[3], &bytes[4], &bytes[5]) != 6) {
        return -1;
    }
    for (int i = 0; i < 6; i++) {
        hw_addr[i] = (char) bytes[i];
    }
    return 0;
}

AIRPLAY_API int airplay_start(const char *name, const char *device_id, int width, int height, int fps,
                              int server_port, const airplay_callbacks_t *cbs) {
    char hw_addr[6];
    int error = 0;
    if (raop) {
        return 0;
    }
    callbacks = *cbs;
    if (parse_device_id(device_id, hw_addr)) {
        log_line(2, "Bad device id %s", device_id ? device_id : "(none)");
        return -1;
    }
    if (!lock_ready) {
        MUTEX_CREATE(lock);
        COND_CREATE(watchdog_wake);
        lock_ready = true;
    }
#ifdef _WIN32
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);
    raop_ntp_global_init();
#endif

    dnssd = dnssd_init(name, (int) strlen(name), hw_addr, sizeof(hw_addr), 0, &error);
    if (!dnssd || error) {
        log_line(2, "Couldn't start DNS-SD (%d)", error);
        return -2;
    }
    /* UxPlay's default features (dnssdint.h), set the way uxplay.cpp sets them: screen mirroring
       and audio, with legacy pairing; no HLS video, no H.265. */
    dnssd_set_airplay_features(dnssd, 0, 0);
    dnssd_set_airplay_features(dnssd, 1, 1);
    dnssd_set_airplay_features(dnssd, 2, 1);
    dnssd_set_airplay_features(dnssd, 3, 0);
    dnssd_set_airplay_features(dnssd, 4, 0);
    dnssd_set_airplay_features(dnssd, 27, 1);
    dnssd_set_airplay_features(dnssd, 42, 0);

    raop_callbacks_t raop_cbs;
    memset(&raop_cbs, 0, sizeof(raop_cbs));
    raop_cbs.conn_init = on_conn_init;
    raop_cbs.conn_destroy = on_conn_destroy;
    raop_cbs.conn_reset = on_conn_reset;
    raop_cbs.conn_feedback = on_conn_feedback;
    raop_cbs.audio_process = on_audio_process;
    raop_cbs.video_process = on_video_process;
    raop_cbs.audio_get_format = on_audio_get_format;
    raop_cbs.audio_set_client_volume = on_audio_set_client_volume;
    raop_cbs.audio_set_volume = on_audio_set_volume;
    raop_cbs.video_reset = on_video_reset;
    raop_cbs.video_set_codec_and_size = on_video_set_codec_and_size;
    raop_cbs.report_client_request = on_report_client_request;
    raop_cbs.passwd = on_passwd;
    raop_cbs.check_register = on_check_register;

    raop = raop_init(&raop_cbs);
    if (!raop) {
        log_line(2, "Couldn't start the AirPlay server");
        airplay_stop();
        return -3;
    }
    raop_set_log_callback(raop, on_log, NULL);
    raop_set_log_level(raop, LOGGER_DEBUG);
    if (raop_init2(raop, 1, device_id, "")) {
        log_line(2, "Couldn't set up the AirPlay server");
        free(raop);
        raop = NULL;
        airplay_stop();
        return -3;
    }
    raop_set_plist(raop, "width", width);
    raop_set_plist(raop, "height", height);
    raop_set_plist(raop, "refreshRate", fps);
    raop_set_plist(raop, "maxFPS", fps);

    /* The server's port (tcp[1]); the mirroring stream's and the rest are assigned as needed. */
    unsigned short tcp[3] = { 0, (unsigned short) server_port, 0 };
    unsigned short udp[3] = { 0, 0, 0 };
    raop_set_tcp_ports(raop, tcp);
    raop_set_udp_ports(raop, udp);
    unsigned short port = raop_get_port(raop);
    if (raop_start_httpd(raop, &port) < 0) {
        log_line(2, "Couldn't listen on port %d", port);
        airplay_stop();
        return -4;
    }
    raop_set_port(raop, port);
    raop_set_dnssd(raop, dnssd);

    int raop_error = dnssd_register_raop(dnssd, port);
    int airplay_error = raop_error ? 0 : dnssd_register_airplay(dnssd, port);
    if (raop_error || airplay_error) {
        /* A negative socket error from mdnsd: UDP port 5353 couldn't be opened, say. */
        log_line(2, "Couldn't advertise the receiver (%d)", raop_error ? raop_error : airplay_error);
        airplay_stop();
        return -5;
    }
    log_line(1, "AirPlay receiver \"%s\" on port %d", name, port);

    watchdog_running = true;
    THREAD_CREATE(watchdog, watchdog_main, NULL);
    return 0;
}

AIRPLAY_API void airplay_stop(void) {
    if (watchdog_running) {
        MUTEX_LOCK(lock);
        watchdog_running = false;
        COND_SIGNAL(watchdog_wake);
        MUTEX_UNLOCK(lock);
        THREAD_JOIN(watchdog);
    }
    if (dnssd) {
        dnssd_unregister_raop(dnssd);
        dnssd_unregister_airplay(dnssd);
    }
    if (raop) {
        raop_destroy(raop);
        raop = NULL;
    }
    if (dnssd) {
        dnssd_destroy(dnssd);
        dnssd = NULL;
    }
    audio_decoder_close();
    open_connections = 0;
    remote_clock_offset = 0;
}
