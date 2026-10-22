/*
 * pvraw — LibRaw 薄封装实现。
 * 输出参数：8 位、相机白平衡、sRGB 或 DCI-P3(D65) 原色 + sRGB 传输曲线、user_flip=0（不旋转）。
 * 曝光：关闭 LibRaw 自带的 auto-bright，改用同一套直方图白点规则并限制最大提升
 *       （见 kMaxAutoLift），因此同一文件在任何线程/设备上渲染结果一致。
 * 注意：process 时 raw2image_start 会把 sizes.flip 改成 user_flip，
 * 所以相机方向必须在 open 后立即取走，否则会丢。
 */
#include "pvraw.h"

#include <atomic>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <new>
#include <string>

#include <libraw/libraw.h>

namespace {

// 自动曝光允许的最大提升倍数（≈1.3 EV）。LibRaw 的 auto-bright 会把 1% 分位映射到白，
// 低反差的明亮场景（如整面白墙）会被推到过曝；平台参考渲染（macOS ImageIO）不会。
// 超过这个界限就保留原曝光：宁可略暗，也不把画面洗白。
constexpr double kMaxAutoLift = 2.5;

// 暴露 LibRaw 的 protected 成员：copy_mem_image 的自动亮度步进需要重建 gamma 曲线。
class PvLibRaw : public LibRaw {
public:
    void pvGammaCurve(double pwr, double ts, int mode, int imax) {
        gamma_curve(pwr, ts, mode, imax);
    }
    // dcraw_process 的 convert_to_rgb 里填充；未处理时可能为 NULL。
    int (*pvHistogram())[LIBRAW_HISTOGRAM_SIZE] {
        return libraw_internal_data.output_data.histogram;
    }
};

struct PvHandle {
    PvLibRaw raw;
    std::atomic<int32_t> cancel{0};
    int32_t camera_flip = 0;
    bool unpacked = false;   // 原始数据是否仍在（同一句柄可按不同参数重复 process）
    bool processed = false;
    bool dead = false;       // process 失败/取消后 LibRaw 已回收输入流，句柄不可再用
    std::string err;

    void setError(const char *msg, int code) {
        err = msg ? msg : "error";
        if (code != LIBRAW_SUCCESS) {
            err += ": ";
            err += LibRaw::strerror(code);
        }
    }
};

// 进度回调是唯一的取消通道：返回非零 → LibRaw 抛 CANCELLED_BY_CALLBACK。
int pvProgressCb(void *data, enum LibRaw_progress, int, int) {
    auto *h = static_cast<PvHandle *>(data);
    return h && h->cancel.load(std::memory_order_relaxed) ? 1 : 0;
}

inline PvHandle *H(void *h) { return static_cast<PvHandle *>(h); }

// LibRaw 的 sizes.flip 通常是 dcraw 编码（0/1/2/3/4/5/6/7），极少数文件是角度值，
// 统一成 dcraw 编码，调用方只需处理 0..7。
int32_t normalizeFlip(int32_t flip) {
    switch (flip) {
        case 90: return 6;
        case 180: return 3;
        case 270: return 5;
        default: return flip;
    }
}

void fillInfo(const PvHandle *ph, pv_raw_info *out) {
    if (!out) return;
    const libraw_image_sizes_t &s = ph->raw.imgdata.sizes;
    out->flip = normalizeFlip(ph->camera_flip);
    out->raw_width = s.raw_width;
    out->raw_height = s.raw_height;
    if (ph->processed) {  // 实际输出尺寸（half_size 已生效，未旋转）
        out->width = s.iwidth;
        out->height = s.iheight;
    } else {
        out->width = s.width;
        out->height = s.height;
    }
}

// copy_mem_image 的自动白点：统计各通道“累计像素数超过 perc”的亮度分位（≤0x2000）。
int autoWhitePoint(PvHandle *ph) {
    int (*hist)[LIBRAW_HISTOGRAM_SIZE] = ph->raw.pvHistogram();
    if (!hist) return 0x2000;
    const libraw_image_sizes_t &S = ph->raw.imgdata.sizes;
    const libraw_output_params_t &O = ph->raw.imgdata.params;
    int perc = int(S.width * S.height * O.auto_bright_thr);
    if (ph->raw.is_fuji_rotated()) perc /= 2;
    const int colors = ph->raw.imgdata.idata.colors;
    int t_white = 0;
    for (int c = 0; c < colors; c++) {
        int val, total;
        for (val = 0x2000, total = 0; --val > 32;)
            if ((total += hist[c][val]) > perc) break;
        if (t_white < val) t_white = val;
    }
    return t_white;
}

}  // namespace

extern "C" {

int32_t pv_raw_version(void) { return LIBRAW_VERSION; }

void *pv_raw_open(const uint8_t *data, int64_t len) {
    if (!data || len <= 0) return nullptr;

    auto *ph = new (std::nothrow) PvHandle();
    if (!ph) return nullptr;

    int rc = ph->raw.open_buffer(data, static_cast<size_t>(len));
    if (rc != LIBRAW_SUCCESS) {
        ph->setError("open_buffer", rc);
        delete ph;
        return nullptr;
    }

    // open_buffer 内部调过 initdata()，参数必须在其后设置。
    libraw_output_params_t &O = ph->raw.imgdata.params;
    O.output_bps = 8;
    O.output_color = 1;    // sRGB 原色（调用方可用 pv_raw_set_output_color 换成 7 = DCI-P3 D65）
    O.gamm[0] = 1.0 / 2.4; // sRGB 传输曲线
    O.gamm[1] = 12.92;
    O.use_camera_wb = 1;
    O.user_flip = 0;  // 保持未旋转数据
    O.user_qual = -1; // LibRaw 默认（AHD）
    O.half_size = 0;
    // 关闭 LibRaw 自带 auto-bright：process 结束时我们自己按同一规则算增益并限幅。
    O.no_auto_bright = 1;
    O.bright = 1.0f;

    // Sony ARW 自带 raw_inset_crops[0]（相机成像区，如 7008x4672）。
    // LibRaw 默认不套用，会把传感器可见区（7028x4688）当输出；套用后与相机 JPEG 取景一致。
    ph->raw.adjust_to_raw_inset_crop(1);

    ph->camera_flip = ph->raw.imgdata.sizes.flip;
    ph->raw.set_progress_handler(pvProgressCb, ph);
    return ph;
}

int32_t pv_raw_get_info(void *h, pv_raw_info *out) {
    if (!h || !out) return LIBRAW_BAD_CROP;
    fillInfo(H(h), out);
    return LIBRAW_SUCCESS;
}

int32_t pv_raw_process(void *h, int32_t half_size, int32_t quality, pv_raw_info *out) {
    if (!h) return LIBRAW_BAD_CROP;
    PvHandle *ph = H(h);
    if (ph->dead) {
        ph->setError("handle invalidated by a previous failure; reopen the file",
                     LIBRAW_INPUT_CLOSED);
        return LIBRAW_INPUT_CLOSED;
    }
    ph->cancel.store(0, std::memory_order_relaxed);
    ph->processed = false;

    libraw_output_params_t &O = ph->raw.imgdata.params;
    O.half_size = half_size ? 1 : 0;
    O.user_qual = quality;

    // 同一句柄可换参数重复用：unpack 只做一次（LibRaw 会以 rawdata 为基准重建 image）。
    int rc = LIBRAW_SUCCESS;
    if (!ph->unpacked) {
        rc = ph->raw.unpack();
        if (rc != LIBRAW_SUCCESS) {
            ph->setError("unpack", rc);
            ph->unpacked = false;
            ph->dead = true;  // 错误路径里 LibRaw 可能已 recycle()
            return rc;
        }
        ph->unpacked = true;
    }
    rc = ph->raw.dcraw_process();
    if (rc != LIBRAW_SUCCESS) {
        ph->setError("dcraw_process", rc);
        // 异常路径里 LibRaw 已 recycle()（含输入流），rawdata 不再有效
        ph->unpacked = false;
        ph->processed = false;
        ph->dead = true;
        return rc;
    }
    ph->processed = true;

    // 自动曝光：与 copy_mem_image 相同的白点规则，但限制提升倍数（kMaxAutoLift）。
    // gain = 65536 / imax，落在 imgdata.params.bright 上，copy 路径与 LibRaw 的
    // copy_mem_image 都会算出同一个 imax（int((0x2000<<3)/bright)），故bit 级一致。
    {
        int imax_auto = autoWhitePoint(ph) * 8;               // 不限幅时 LibRaw 会用这个
        int imax_cap = int(65536.0 / kMaxAutoLift + 0.5);     // 限幅后的最小白点
        int imax = imax_auto > imax_cap ? imax_auto : imax_cap;
        if (imax > 65536) imax = 65536;
        if (imax < 512) imax = 512;
        // +0.5 保证 int(65536/bright) 截断回同一个 imax
        O.bright = float(65536.0 / (imax + 0.5));
    }

    fillInfo(ph, out);
    return LIBRAW_SUCCESS;
}

int32_t pv_raw_copy_rgba8(void *h, uint8_t *dst, int32_t stride) {
    if (!h || !dst) return LIBRAW_BAD_CROP;
    PvHandle *ph = H(h);
    if (!ph->processed || !ph->raw.imgdata.image) {
        ph->setError("copy_rgba8: dcraw_process not completed", LIBRAW_OUT_OF_ORDER_CALL);
        return LIBRAW_OUT_OF_ORDER_CALL;
    }

    libraw_colordata_t &C = ph->raw.imgdata.color;
    libraw_output_params_t &O = ph->raw.imgdata.params;
    libraw_image_sizes_t &S = ph->raw.imgdata.sizes;

    // 复刻 copy_mem_image：按直方图定白点后重建 gamma 曲线。
    // 我们的参数是 no_auto_bright=1，t_white 保持 0x2000，imax 由 bright（已限幅）决定。
    int (*hist)[LIBRAW_HISTOGRAM_SIZE] = ph->raw.pvHistogram();
    if (hist) {
        int t_white = 0x2000;
        if (!((O.highlight & ~2) || O.no_auto_bright)) {
            t_white = autoWhitePoint(ph);
        }
        ph->raw.pvGammaCurve(O.gamm[0], O.gamm[1], 2, int((t_white << 3) / O.bright));
    }

    // flip 为 0，copy_mem_image 的 soff/cstep/rstep 退化为行主序线性遍历。
    const ushort *curve = C.curve;
    const ushort(*src)[4] = ph->raw.imgdata.image;
    const int32_t w = S.iwidth;
    const int32_t ht = S.iheight;
    const int colors = ph->raw.imgdata.idata.colors;

    for (int32_t y = 0; y < ht; ++y) {
        uint8_t *p = dst + static_cast<size_t>(y) * static_cast<size_t>(stride);
        const ushort(*row)[4] = src + static_cast<size_t>(y) * static_cast<size_t>(w);
        for (int32_t x = 0; x < w; ++x) {
            if (colors >= 3) {
                p[0] = static_cast<uint8_t>(curve[row[x][0]] >> 8);
                p[1] = static_cast<uint8_t>(curve[row[x][1]] >> 8);
                p[2] = static_cast<uint8_t>(curve[row[x][2]] >> 8);
            } else {  // 单色传感器
                p[0] = p[1] = p[2] = static_cast<uint8_t>(curve[row[x][0]] >> 8);
            }
            p[3] = 255;
            p += 4;
        }
    }
    return LIBRAW_SUCCESS;
}

void pv_raw_cancel(void *h) {
    if (h) H(h)->cancel.store(1, std::memory_order_relaxed);
}

void pv_raw_set_output_color(void *h, int32_t libraw_output_color) {
    if (!h) return;
    H(h)->raw.imgdata.params.output_color = libraw_output_color;
}

double pv_raw_exposure_gain(void *h) {
    return h ? double(H(h)->raw.imgdata.params.bright) : 1.0;
}

int32_t pv_raw_thumb_count(void *h) {
    return h ? H(h)->raw.imgdata.thumbs_list.thumbcount : 0;
}

int32_t pv_raw_thumb_get_info(void *h, int32_t index, pv_raw_thumb_info *out) {
    if (!h || !out) return LIBRAW_BAD_CROP;
    const libraw_thumbnail_list_t &list = H(h)->raw.imgdata.thumbs_list;
    if (index < 0 || index >= list.thumbcount || index >= LIBRAW_THUMBNAIL_MAXCOUNT)
        return LIBRAW_REQUEST_FOR_NONEXISTENT_THUMBNAIL;
    const libraw_thumbnail_item_t &t = list.thumblist[index];
    out->width = t.twidth;
    out->height = t.theight;
    out->format = int32_t(t.tformat); // LibRaw 内部枚举：4 = JPEG
    out->bytes = int32_t(t.tlength);
    return LIBRAW_SUCCESS;
}

int32_t pv_raw_thumb_unpack(void *h, int32_t index) {
    if (!h) return LIBRAW_BAD_CROP;
    PvHandle *ph = H(h);
    int rc = ph->raw.unpack_thumb_ex(index);
    if (rc != LIBRAW_SUCCESS) {
        ph->err = "unpack_thumb: ";
        ph->err += LibRaw::strerror(rc);
    }
    return rc;
}

const uint8_t *pv_raw_thumb_data(void *h, int32_t *out_len) {
    if (out_len) *out_len = 0;
    if (!h) return nullptr;
    const libraw_thumbnail_t &t = H(h)->raw.imgdata.thumbnail;
    if (!t.thumb || t.tlength == 0) return nullptr;
    if (out_len) *out_len = int32_t(t.tlength);
    return reinterpret_cast<const uint8_t *>(t.thumb);
}

void pv_raw_close(void *h) {
    if (!h) return;
    PvHandle *ph = H(h);
    ph->raw.recycle();
    delete ph;
}

const char *pv_raw_last_error(void *h) {
    if (!h) return "null handle";
    return H(h)->err.c_str();
}

}  // extern "C"
