/*
 * pvhevc — minimal FFmpeg-backed HEVC still-picture decoder for PhotoViewer.
 *
 * One pv_hevc context decodes one access unit (a complete still picture) at a
 * time.  A context is not thread safe; use one context per thread.
 */
#ifndef PVHEVC_H
#define PVHEVC_H

#include <stdint.h>

#define PV_API __declspec(dllexport)

#ifdef __cplusplus
extern "C" {
#endif

typedef struct pv_hevc_frame {
    int32_t width, height;          /* decoded picture size after conformance-window crop */
    int32_t bit_depth;              /* 8, 10, 12 */
    int32_t chroma_format;          /* 0 = mono(gray), 1 = 4:2:0, 2 = 4:2:2, 3 = 4:4:4 */
    const uint8_t* planes[3];       /* Y, Cb, Cr; uint8 when bit_depth==8 else LE uint16 */
    int32_t strides[3];             /* bytes per row */
    int32_t full_range;             /* 1 = full (pc/jpeg) range per bitstream VUI, 0 = limited/unspecified */
    int32_t matrix, primaries, transfer; /* ISO/IEC 23091-2 code points from VUI (2 = unspecified) */
} pv_hevc_frame;

/* Returns LIBAVCODEC_VERSION_INT of the statically linked FFmpeg. */
PV_API int32_t pv_hevc_version(void);

/* threads: avctx->thread_count (1 = single thread; caller parallelises across tiles). */
PV_API void* pv_hevc_open(int32_t threads);

/*
 * input: one complete still picture as an Annex-B byte stream
 *        (VPS/SPS/PPS + slice NALs with 00 00 00 01 start codes).
 * Returns 0 on success, negative AVERROR on failure.
 * out->planes stay valid until the next pv_hevc_decode or pv_hevc_close.
 */
PV_API int32_t pv_hevc_decode(void* ctx, const uint8_t* annexb, int32_t length, pv_hevc_frame* out);

PV_API void pv_hevc_close(void* ctx);

/* Human readable message for the last failure on this context; "" if none. */
PV_API const char* pv_hevc_last_error(void* ctx);

#ifdef __cplusplus
}
#endif

#endif /* PVHEVC_H */
