/*
 * pvraw — LibRaw 极简 C ABI（Android arm64 / iOS 复用）。
 * 目标：Sony ARW 全分辨率解码 → 无旋转的 RGBA8（sRGB 原色 + sRGB 传输曲线），
 * 方向由上层（GPU）应用，本层只报告 LibRaw 的相机 flip 值。
 * 线程模型：一个句柄只在一个线程使用；不同句柄可并发。
 */
#ifndef PVRAW_H
#define PVRAW_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(__GNUC__) || defined(__clang__)
#define PVRAW_API __attribute__((visibility("default")))
#else
#define PVRAW_API
#endif

/* 打开后 / 处理后回填的尺寸与方向信息。 */
typedef struct pv_raw_info {
    int32_t width;      /* 未旋转的像素宽（open 后为裁剪尺寸，process 后为实际输出尺寸） */
    int32_t height;     /* 未旋转的像素高 */
    int32_t flip;       /* LibRaw sizes.flip，相机方向（0/3/5/6 等），始终为原始值 */
    int32_t raw_width;  /* 传感器原始宽（含边缘） */
    int32_t raw_height; /* 传感器原始高（含边缘） */
} pv_raw_info;

/* LIBRAW_VERSION 数值。 */
PVRAW_API int32_t pv_raw_version(void);

/* open_buffer；data 由调用方持有，close 之前不得释放。失败返回 NULL。 */
PVRAW_API void *pv_raw_open(const uint8_t *data, int64_t len);

/* 打开后的信息（未处理）。返回 0 或 LibRaw 错误码。 */
PVRAW_API int32_t pv_raw_get_info(void *h, pv_raw_info *out);

/* unpack + dcraw_process。half_size: 0/1；quality: -1 默认，0/2/3/11 等同 LibRaw。
 * 成功后 out 为实际输出尺寸（未旋转）与相机 flip。返回 0 或 LibRaw 错误码。
 * 同一句柄可换参数重复调用（unpack 只做一次）；但任一次失败或被取消后句柄即失效
 * （LibRaw 会回收输入流），需 pv_raw_close 后重新 open。 */
PVRAW_API int32_t pv_raw_process(void *h, int32_t half_size, int32_t quality, pv_raw_info *out);

/* 把处理结果写成 RGBA8（A=255），不旋转，行自上而下。stride 为字节数。 */
PVRAW_API int32_t pv_raw_copy_rgba8(void *h, uint8_t *dst, int32_t stride);

/* 输出原色：LibRaw 的 output_color（1 = sRGB，7 = DCI-P3 D65，即 Display P3）。
 * 必须在 open 之后、process 之前调用；传输曲线始终是 sRGB。 */
PVRAW_API void pv_raw_set_output_color(void *h, int32_t libraw_output_color);

/* 本次 process 采用的自动曝光增益（线性倍数，≤ kMaxAutoLift）。 */
PVRAW_API double pv_raw_exposure_gain(void *h);

/* 内嵌缩略图/预览（不解 RAW、不做色彩处理，直接给原始 JPEG 字节）。 */
typedef struct pv_raw_thumb_info {
    int32_t width;  // 未旋转
    int32_t height;
    int32_t format; // LibRaw 内部枚举：4 = JPEG
    int32_t bytes;
} pv_raw_thumb_info;

PVRAW_API int32_t pv_raw_thumb_count(void *h);
PVRAW_API int32_t pv_raw_thumb_get_info(void *h, int32_t index, pv_raw_thumb_info *out);
/* 解出指定缩略图（之后用 pv_raw_thumb_data 取字节）。 */
PVRAW_API int32_t pv_raw_thumb_unpack(void *h, int32_t index);
/* 已解出的缩略图字节（JPEG 原样），句柄内存储，下次 unpack/close 前有效。 */
PVRAW_API const uint8_t *pv_raw_thumb_data(void *h, int32_t *out_len);

/* 置取消标志；进度回调返回非零，LibRaw 以 LIBRAW_CANCELLED_BY_CALLBACK 中止。 */
PVRAW_API void pv_raw_cancel(void *h);

/* recycle + delete。 */
PVRAW_API void pv_raw_close(void *h);

/* 最近一次错误文本（句柄内静态存储，close 后失效）。 */
PVRAW_API const char *pv_raw_last_error(void *h);

#ifdef __cplusplus
}
#endif

#endif /* PVRAW_H */
