/*
 * pvhevc — minimal FFmpeg-backed HEVC still-picture decoder for PhotoViewer.
 *
 * Static FFmpeg build: libavcodec.a + libavutil.a, HEVC decoder only, LGPL.
 * One source, two build scripts (the header exports the identical C ABI):
 *   PhotoViewer.Windows/build-native-windows-hevc.ps1  → pvhevc.dll
 *   PhotoViewer.Android/build-native-android-hevc.sh   → libpvhevc.so
 * Each script's pvhevc.txt records the exact configure line and version.
 */

#include "pvhevc.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#else
#include <stdatomic.h>
#endif

#include <libavcodec/avcodec.h>
#include <libavutil/error.h>
#include <libavutil/frame.h>
#include <libavutil/log.h>
#include <libavutil/pixdesc.h>

typedef struct PvHevcContext {
    AVCodecContext *avctx;
    AVFrame        *frame;      /* holds the current output picture reference */
    AVFrame        *scratch;    /* drained frames we discard */
    AVPacket       *pkt;
    char            err[256];
    int             draining;   /* decoder already saw a flush packet */
} PvHevcContext;

static void pv_set_error(PvHevcContext *c, const char *what, int averr)
{
    char detail[160] = "";
    if (averr < 0)
        av_strerror(averr, detail, sizeof(detail));
    if (detail[0])
        snprintf(c->err, sizeof(c->err), "%s: %s (%d)", what, detail, averr);
    else
        snprintf(c->err, sizeof(c->err), "%s", what);
}

/* FFmpeg logs through a process-wide level that has no per-context override.
 * Every thread stores the same constant, so this is idempotent and race free;
 * callers that want FFmpeg chatter can raise the level again after pv_hevc_open. */
static void pv_quiet_logs_once(void)
{
#ifdef _WIN32
    static volatile long done = 0;
    if (InterlockedCompareExchange(&done, 1, 0) != 0)
        return;
#else
    static atomic_int done = 0;
    if (atomic_exchange_explicit(&done, 1, memory_order_relaxed) != 0)
        return;
#endif
    av_log_set_level(AV_LOG_ERROR);
}

static int pv_chroma_format(const AVPixFmtDescriptor *desc)
{
    if (!desc || desc->nb_components < 3)
        return 0; /* monochrome / gray */
    if (desc->log2_chroma_w == 1 && desc->log2_chroma_h == 1)
        return 1; /* 4:2:0 */
    if (desc->log2_chroma_w == 1 && desc->log2_chroma_h == 0)
        return 2; /* 4:2:2 */
    if (desc->log2_chroma_w == 0 && desc->log2_chroma_h == 0)
        return 3; /* 4:4:4 */
    return 0;
}

PV_API int32_t pv_hevc_version(void)
{
    return LIBAVCODEC_VERSION_INT;
}

PV_API void *pv_hevc_open(int32_t threads)
{
    pv_quiet_logs_once();

    const AVCodec *codec = avcodec_find_decoder(AV_CODEC_ID_HEVC);
    if (!codec)
        return NULL;

    PvHevcContext *c = (PvHevcContext *)calloc(1, sizeof(*c));
    if (!c)
        return NULL;

    c->avctx = avcodec_alloc_context3(codec);
    c->frame = av_frame_alloc();
    c->scratch = av_frame_alloc();
    c->pkt = av_packet_alloc();
    if (!c->avctx || !c->frame || !c->scratch || !c->pkt)
        goto fail;

    if (threads < 1)
        threads = 1;
    c->avctx->thread_count = threads;
    /* Slice threading keeps a single still picture self-contained; frame
     * threading would also work because we always drain, but adds latency. */
    c->avctx->thread_type = threads > 1 ? FF_THREAD_SLICE : 0;

    if (avcodec_open2(c->avctx, codec, NULL) < 0) {
        pv_set_error(c, "avcodec_open2 failed", AVERROR_UNKNOWN);
        goto fail;
    }
    return c;

fail:
    if (c->pkt)
        av_packet_free(&c->pkt);
    if (c->scratch)
        av_frame_free(&c->scratch);
    if (c->frame)
        av_frame_free(&c->frame);
    if (c->avctx)
        avcodec_free_context(&c->avctx);
    free(c);
    return NULL;
}

PV_API int32_t pv_hevc_decode(void *ctx, const uint8_t *annexb, int32_t length, pv_hevc_frame *out)
{
    PvHevcContext *c = (PvHevcContext *)ctx;

    if (!out)
        return AVERROR(EINVAL);
    memset(out, 0, sizeof(*out));
    if (!c || !annexb || length <= 0) {
        if (c)
            pv_set_error(c, "invalid arguments", AVERROR(EINVAL));
        return AVERROR(EINVAL);
    }
    c->err[0] = '\0';

    /* Releasing the previous picture is what makes the documented lifetime
     * ("valid until the next pv_hevc_decode") true.  The frame's buffers go
     * back to the decoder's pool, which is why we avoid avcodec_flush_buffers
     * on the common path: flushing drops that pool and every picture would
     * re-allocate (and re-commit) the whole 22 MB surface set. */
    av_frame_unref(c->frame);

    if (c->draining) {
        /* Previous picture needed a drain; FFmpeg requires a reset before reuse. */
        avcodec_flush_buffers(c->avctx);
        c->draining = 0;
    }

    int ret = av_new_packet(c->pkt, length);
    if (ret < 0) {
        pv_set_error(c, "av_new_packet failed", ret);
        return ret;
    }
    memcpy(c->pkt->data, annexb, (size_t)length);

    for (;;) {
        ret = avcodec_send_packet(c->avctx, c->pkt);
        if (ret != AVERROR(EAGAIN))
            break;
        /* Decoder wants us to consume before accepting more input. */
        av_frame_unref(c->scratch);
        if (avcodec_receive_frame(c->avctx, c->scratch) < 0)
            break;
    }
    av_packet_unref(c->pkt);
    if (ret < 0 && ret != AVERROR_EOF) {
        pv_set_error(c, "avcodec_send_packet failed", ret);
        return ret;
    }

    /* A still picture is a single access unit and comes straight out of the
     * decoder.  Only when the decoder decides to buffer it do we flush with a
     * null packet; that path also tells the next call to reset the decoder. */
    ret = avcodec_receive_frame(c->avctx, c->frame);
    if (ret == AVERROR(EAGAIN) || ret == AVERROR_EOF) {
        int fret = avcodec_send_packet(c->avctx, NULL);
        if (fret < 0 && fret != AVERROR_EOF) {
            pv_set_error(c, "flush failed", fret);
            return fret;
        }
        c->draining = 1;
        ret = avcodec_receive_frame(c->avctx, c->frame);
    }
    if (ret < 0) {
        pv_set_error(c, "no frame produced", ret);
        return ret;
    }

    const AVPixFmtDescriptor *desc = av_pix_fmt_desc_get((enum AVPixelFormat)c->frame->format);
    if (!desc) {
        pv_set_error(c, "unknown pixel format", AVERROR_UNKNOWN);
        return AVERROR_UNKNOWN;
    }

    out->width = c->frame->width;
    out->height = c->frame->height;
    out->bit_depth = desc->comp[0].depth;
    out->chroma_format = pv_chroma_format(desc);
    for (int i = 0; i < 3; i++) {
        out->planes[i] = c->frame->data[i];
        out->strides[i] = c->frame->linesize[i];
    }
    out->full_range = c->frame->color_range == AVCOL_RANGE_JPEG ? 1 : 0;

    out->matrix = c->frame->colorspace != AVCOL_SPC_UNSPECIFIED
                      ? c->frame->colorspace
                      : c->avctx->colorspace;
    out->primaries = c->frame->color_primaries != AVCOL_PRI_UNSPECIFIED
                         ? c->frame->color_primaries
                         : c->avctx->color_primaries;
    out->transfer = c->frame->color_trc != AVCOL_TRC_UNSPECIFIED
                        ? c->frame->color_trc
                        : c->avctx->color_trc;
    return 0;
}

PV_API void pv_hevc_close(void *ctx)
{
    PvHevcContext *c = (PvHevcContext *)ctx;
    if (!c)
        return;
    if (c->pkt)
        av_packet_free(&c->pkt);
    if (c->scratch)
        av_frame_free(&c->scratch);
    if (c->frame)
        av_frame_free(&c->frame);
    if (c->avctx)
        avcodec_free_context(&c->avctx);
    free(c);
}

PV_API const char *pv_hevc_last_error(void *ctx)
{
    PvHevcContext *c = (PvHevcContext *)ctx;
    return c ? c->err : "";
}
