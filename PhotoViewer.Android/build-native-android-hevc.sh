#!/usr/bin/env bash
# 构建 Android arm64-v8a 原生 HEVC 解码库（libpvhevc.so）：
# 静态 FFmpeg（仅 HEVC 解码器 + parser）+ 薄 C 封装 pvhevc.c。
#
# 与 PhotoViewer.Windows/build-native-windows-hevc.ps1 同源同契约（pvhevc.h 的 5 个导出）：
#   · FFmpeg 只保留 libavcodec + libavutil，仅启用 hevc 解码器与 hevc parser
#   · libavcodec.a / libavutil.a 静态链接进 libpvhevc.so，运行时只依赖 bionic 系统库
#   · aarch64 NEON + 汇编提供 HEVC 8/10/12-bit 加速（不用 --enable-x86asm）
#   · LGPL 构建：不传 --enable-gpl / --enable-nonfree，随附 FFmpeg 许可证文本
#
# 用法：
#   bash PhotoViewer.Android/build-native-android-hevc.sh
#   FFMPEG_VERSION=8.1.3 JOBS=8 bash PhotoViewer.Android/build-native-android-hevc.sh
#
# 依赖环境：
#   · Android NDK（$ANDROID_NDK_HOME，或 ~/Library/Android/sdk/ndk/<最新版本>）
#   · 网络连接（从 https://ffmpeg.org/releases/ 下载源码 tarball）
#
# 产出：
#   NativeLibs/arm64-v8a/libpvhevc.so
#   NativeLibs/arm64-v8a/pvhevc.txt
#   NativeLibs/arm64-v8a/pvhevc-FFmpeg-LICENSE.txt
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

SHIM_DIR="$REPO_ROOT/PhotoViewer.Windows/NativeSrc/pvhevc"
OUTPUT_DIR="$SCRIPT_DIR/NativeLibs/arm64-v8a"

FFMPEG_VERSION="${FFMPEG_VERSION:-8.1.3}"
API_LEVEL="${API_LEVEL:-24}"          # 与 csproj 的 SupportedOSPlatformVersion 一致
BUILD_DIR="${PV_HEVC_BUILD_DIR:-$SCRIPT_DIR/obj/native-hevc}"   # obj/ 已被 .gitignore 忽略
JOBS="${JOBS:-$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 4)}"

# ════════════════════════════════════════════════════════════
# 工具探测
# ════════════════════════════════════════════════════════════

# 定位 NDK：优先 $ANDROID_NDK_HOME，否则取 SDK 下版本号最大的目录。
find_ndk() {
    if [[ -n "${ANDROID_NDK_HOME:-}" && -d "$ANDROID_NDK_HOME" ]]; then
        echo "$ANDROID_NDK_HOME"
        return
    fi
    local sdk="${ANDROID_SDK_ROOT:-${ANDROID_HOME:-$HOME/Library/Android/sdk}}"
    local ndk_root="$sdk/ndk"
    [[ -d "$ndk_root" ]] || return 1
    local newest
    newest="$(ls -1 "$ndk_root" | sort -V | tail -n 1)"
    [[ -n "$newest" && -d "$ndk_root/$newest" ]] || return 1
    echo "$ndk_root/$newest"
}

NDK="$(find_ndk || true)"
if [[ -z "$NDK" ]]; then
    echo "错误：找不到 Android NDK。请设置 ANDROID_NDK_HOME，或安装到 ~/Library/Android/sdk/ndk/。" >&2
    exit 1
fi

# NDK 的 macOS 主机工具链目录名为 darwin-x86_64（Apple Silicon 上也一样用 Rosetta 版预编译产物）
HOST_TAG="darwin-x86_64"
TOOLCHAIN="$NDK/toolchains/llvm/prebuilt/$HOST_TAG"
if [[ ! -d "$TOOLCHAIN" ]]; then
    HOST_TAG="darwin-arm64"
    TOOLCHAIN="$NDK/toolchains/llvm/prebuilt/$HOST_TAG"
fi
if [[ ! -d "$TOOLCHAIN" ]]; then
    echo "错误：NDK 缺少 macOS 主机工具链：$NDK/toolchains/llvm/prebuilt/" >&2
    exit 1
fi

BIN="$TOOLCHAIN/bin"
CC="$BIN/aarch64-linux-android${API_LEVEL}-clang"
CXX="$BIN/aarch64-linux-android${API_LEVEL}-clang++"
NM="$BIN/llvm-nm"
READELF="$BIN/llvm-readelf"
STRIP="$BIN/llvm-strip"
AR="$BIN/llvm-ar"
RANLIB="$BIN/llvm-ranlib"

for t in "$CC" "$CXX" "$NM" "$READELF" "$STRIP" "$AR" "$RANLIB"; do
    if [[ ! -x "$t" ]]; then
        echo "错误：缺少 NDK 工具 $t" >&2
        exit 1
    fi
done

NDK_VERSION="$(sed -n 's/^Pkg.Revision[[:space:]]*=[[:space:]]*//p' "$NDK/source.properties" 2>/dev/null | tr -d '\r' | head -n 1)"
NDK_VERSION="${NDK_VERSION:-unknown}"

echo ""
echo "══════════════════════════════════════════════════════"
echo "  build-native-android-hevc.sh"
echo "  FFmpeg $FFMPEG_VERSION  静态 libavcodec + libavutil（仅 HEVC 解码，LGPL）"
echo "  目标: NativeLibs/arm64-v8a/libpvhevc.so"
echo "  NDK : $NDK_VERSION  ($HOST_TAG, API $API_LEVEL)"
echo "══════════════════════════════════════════════════════"

# ── 目录 ──────────────────────────────────────────────────
DOWNLOADS_DIR="$BUILD_DIR/downloads"
SRC_DIR="$BUILD_DIR/src"
LOG_DIR="$BUILD_DIR/logs"
SHIM_OUT_DIR="$BUILD_DIR/out"
mkdir -p "$DOWNLOADS_DIR" "$SRC_DIR" "$LOG_DIR" "$SHIM_OUT_DIR" "$OUTPUT_DIR"

# ── 下载源码 ──────────────────────────────────────────────
# Windows 脚本未附校验和，这里同样只做完整性检查（tar 能否解开）。
FF_ARCHIVE="$DOWNLOADS_DIR/ffmpeg-$FFMPEG_VERSION.tar.xz"
if [[ ! -f "$FF_ARCHIVE" ]]; then
    echo ""
    echo "─── 下载源码 ───────────────────────────────────────────"
    echo "  下载 FFmpeg $FFMPEG_VERSION ..."
    curl -fsSL --retry 3 -o "$FF_ARCHIVE.part" \
        "https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VERSION.tar.xz"
    mv "$FF_ARCHIVE.part" "$FF_ARCHIVE"
    echo "  ✓ FFmpeg $FFMPEG_VERSION 下载完成"
else
    echo ""
    echo "  [跳过] FFmpeg $FFMPEG_VERSION 已下载"
fi

# ── 解压（仅首次）──────────────────────────────────────────
FF_SRC="$SRC_DIR/ffmpeg-$FFMPEG_VERSION"
if [[ ! -f "$FF_SRC/configure" ]]; then
    echo "  解压 $(basename "$FF_ARCHIVE") ..."
    tar -xf "$FF_ARCHIVE" -C "$SRC_DIR"
fi
if [[ ! -f "$FF_SRC/configure" ]]; then
    echo "错误：FFmpeg 源码目录不完整：$FF_SRC" >&2
    exit 1
fi

# ── configure ─────────────────────────────────────────────
# 只保留解码必需组件；关闭全部程序/网络/滤镜/swscale 以缩小体积。
# aarch64 不需要 x86asm；NEON/汇编在 armv8-a 上默认可用，这里显式打开。
CONFIGURE_ARGS=(
    "--target-os=android"
    "--arch=aarch64"
    "--cpu=armv8-a"
    "--enable-cross-compile"
    "--cc=$CC"
    "--cxx=$CXX"
    "--ar=$AR"
    "--nm=$NM"
    "--ranlib=$RANLIB"
    "--strip=$STRIP"
    "--sysroot=$TOOLCHAIN/sysroot"
    "--disable-everything"
    "--disable-programs"
    "--disable-doc"
    "--disable-network"
    "--disable-autodetect"
    "--disable-debug"
    "--enable-static"
    "--disable-shared"
    "--enable-avcodec"
    "--enable-decoder=hevc"
    "--enable-parser=hevc"
    "--disable-avformat"
    "--disable-swscale"
    "--disable-swresample"
    "--disable-avfilter"
    "--disable-avdevice"
    "--enable-pic"
    "--enable-neon"
    "--enable-asm"
    "--extra-cflags=-O3"
)
CONFIGURE_LINE="./configure ${CONFIGURE_ARGS[*]}"

echo ""
echo "─── configure ──────────────────────────────────────────"
if [[ ! -f "$FF_SRC/ffbuild/config.mak" ]]; then
    ( cd "$FF_SRC" && "$FF_SRC/configure" "${CONFIGURE_ARGS[@]}" ) \
        > "$LOG_DIR/configure.log" 2>&1 || {
            echo "  ✗ configure 失败，日志尾部：" >&2
            tail -n 40 "$LOG_DIR/configure.log" >&2
            exit 1
        }
else
    echo "  [跳过] 已有 ffbuild/config.mak（删除 $BUILD_DIR/src 可强制重配）"
fi
echo "  ✓ configure 完成"

# ── 校验构建配置（HEVC 解码器 + NEON 汇编 + LGPL）──────────
CONFIG_TEXT="$(cat "$FF_SRC/config.h" "$FF_SRC/config_components.h")"
for needle in \
    "#define CONFIG_HEVC_DECODER 1" \
    "#define CONFIG_HEVC_PARSER 1" \
    "#define CONFIG_AVCODEC 1" \
    "#define CONFIG_SWSCALE 0" \
    "#define CONFIG_GPL 0" \
    "#define CONFIG_NONFREE 0" \
    "#define HAVE_NEON 1"; do
    if ! grep -qF "$needle" <<< "$CONFIG_TEXT"; then
        echo "错误：构建配置缺少预期定义：$needle" >&2
        exit 1
    fi
done
if grep -qF "#define CONFIG_GPL 1" <<< "$CONFIG_TEXT" || grep -qF "#define CONFIG_NONFREE 1" <<< "$CONFIG_TEXT"; then
    echo "错误：configure 打开了 GPL / NONFREE" >&2
    exit 1
fi
echo "  ✓ CONFIG_HEVC_DECODER / CONFIG_HEVC_PARSER / HAVE_NEON 均启用，GPL/NONFREE 关闭"

# ── 编译 ─────────────────────────────────────────────────
echo ""
echo "─── 编译 libavcodec / libavutil ────────────────────────"
( cd "$FF_SRC" && make -j"$JOBS" ) > "$LOG_DIR/make.log" 2>&1 || {
    echo "  ✗ make 失败，日志尾部：" >&2
    tail -n 40 "$LOG_DIR/make.log" >&2
    exit 1
}
echo "  ✓ 编译完成"

# HEVC 的 aarch64 NEON 符号存在即证明汇编进了 libavcodec.a
NEON_COUNT="$("$NM" --defined-only "$FF_SRC/libavcodec/libavcodec.a" 2>/dev/null \
    | grep -cE ' [Tt] ff_hevc_[A-Za-z0-9_]*_neon' || true)"
if [[ "${NEON_COUNT:-0}" -le 0 ]]; then
    echo "错误：libavcodec.a 中未找到 HEVC aarch64 NEON 符号，汇编未生效" >&2
    exit 1
fi
echo "  ✓ HEVC aarch64 NEON 符号 $NEON_COUNT 个"

# ── 编译并链接 libpvhevc.so ──────────────────────────────
echo ""
echo "─── 链接 libpvhevc.so ─────────────────────────────────"

# 版本脚本 + -fvisibility=hidden：只导出 pv_hevc_*，FFmpeg 符号不外泄
VERSION_SCRIPT="$SHIM_OUT_DIR/pvhevc.map"
cat > "$VERSION_SCRIPT" <<'EOF'
{
  global:
    pv_hevc_version;
    pv_hevc_open;
    pv_hevc_decode;
    pv_hevc_close;
    pv_hevc_last_error;
  local:
    *;
};
EOF

"$CC" -O3 -fPIC -fvisibility=hidden -ffunction-sections -fdata-sections \
    -Wall -Wextra \
    -c "$SHIM_DIR/pvhevc.c" -o "$SHIM_OUT_DIR/pvhevc.o" \
    -I "$SHIM_DIR" -I "$FF_SRC" \
    > "$LOG_DIR/shim-cc.log" 2>&1 || {
        echo "  ✗ pvhevc.c 编译失败，日志尾部：" >&2
        tail -n 40 "$LOG_DIR/shim-cc.log" >&2
        exit 1
    }

SO_PATH="$SHIM_OUT_DIR/libpvhevc.so"
"$CC" -shared -o "$SO_PATH" "$SHIM_OUT_DIR/pvhevc.o" \
    -Wl,--start-group \
    "$FF_SRC/libavcodec/libavcodec.a" "$FF_SRC/libavutil/libavutil.a" \
    -Wl,--end-group \
    -Wl,-soname,libpvhevc.so \
    -Wl,-z,max-page-size=16384 \
    -Wl,--gc-sections \
    -Wl,--exclude-libs,ALL \
    -Wl,--version-script="$VERSION_SCRIPT" \
    -lm \
    > "$LOG_DIR/link.log" 2>&1 || {
        echo "  ✗ 链接 libpvhevc.so 失败，日志尾部：" >&2
        tail -n 40 "$LOG_DIR/link.log" >&2
        exit 1
    }

"$STRIP" --strip-unneeded "$SO_PATH"

# ── 校验产物 ─────────────────────────────────────────────
echo ""
echo "─── 校验 libpvhevc.so ─────────────────────────────────"

EXPECTED_SYMBOLS="pv_hevc_close pv_hevc_decode pv_hevc_last_error pv_hevc_open pv_hevc_version"
ACTUAL_SYMBOLS="$("$NM" -D --defined-only "$SO_PATH" | awk '{print $NF}' | sort -u | xargs)"
if [[ "$ACTUAL_SYMBOLS" != "$EXPECTED_SYMBOLS" ]]; then
    echo "错误：导出符号不符合预期。" >&2
    echo "  期望：$EXPECTED_SYMBOLS" >&2
    echo "  实际：$ACTUAL_SYMBOLS" >&2
    exit 1
fi
echo "  ✓ 导出符号：$ACTUAL_SYMBOLS"

NEEDED_ALL="$("$READELF" -d "$SO_PATH" | sed -n 's/.*NEEDED.*\[\(.*\)\]/\1/p')"
NEEDED_LIST="$(sort -u <<< "$NEEDED_ALL")"
for lib in $NEEDED_LIST; do
    case "$lib" in
        libc.so|libm.so|libdl.so) ;;
        *)
            echo "错误：libpvhevc.so 依赖了不允许的运行库：$lib" >&2
            echo "  全部 NEEDED：$(tr '\n' ' ' <<< "$NEEDED_LIST")" >&2
            exit 1
            ;;
    esac
done
echo "  ✓ NEEDED 仅 bionic 系统库：$(tr '\n' ' ' <<< "$NEEDED_LIST")"

LOAD_ALIGNS="$("$READELF" -l "$SO_PATH" | awk '$1 == "LOAD" { print $NF }' | sort -u)"
for a in $LOAD_ALIGNS; do
    if [[ "$a" != "0x4000" ]]; then
        echo "错误：LOAD 段对齐为 $a，期望 0x4000（16 KB 页）" >&2
        echo "  全部对齐：$(tr '\n' ' ' <<< "$LOAD_ALIGNS")" >&2
        exit 1
    fi
done
echo "  ✓ LOAD 段对齐：$(tr '\n' ' ' <<< "$LOAD_ALIGNS")"

# ── 产出到 NativeLibs ───────────────────────────────────
echo ""
echo "─── 产出 ──────────────────────────────────────────────"

cp -f "$SO_PATH" "$OUTPUT_DIR/libpvhevc.so"
SO_SHA256="$(shasum -a 256 "$OUTPUT_DIR/libpvhevc.so" | awk '{print $1}')"
SO_SIZE_KB="$(du -k "$OUTPUT_DIR/libpvhevc.so" | awk '{print $1}')"
echo "  ✓ NativeLibs/arm64-v8a/libpvhevc.so  (${SO_SIZE_KB} KB, sha256 $SO_SHA256)"

LICENSE_NAME="COPYING.LGPLv2.1"
if [[ ! -f "$FF_SRC/$LICENSE_NAME" ]]; then
    LICENSE_NAME="COPYING.LGPLv3"
fi
if [[ ! -f "$FF_SRC/$LICENSE_NAME" ]]; then
    echo "错误：找不到 FFmpeg 许可证文本（COPYING.LGPLv2.1 / COPYING.LGPLv3）" >&2
    exit 1
fi
cp -f "$FF_SRC/$LICENSE_NAME" "$OUTPUT_DIR/pvhevc-FFmpeg-LICENSE.txt"
echo "  ✓ NativeLibs/arm64-v8a/pvhevc-FFmpeg-LICENSE.txt  ($LICENSE_NAME)"

cat > "$OUTPUT_DIR/pvhevc.txt" <<EOF
libpvhevc.so — PhotoViewer HEVC still-picture decoder

FFmpeg version : $FFMPEG_VERSION
License        : LGPL 2.1 or later ($LICENSE_NAME); no --enable-gpl / --enable-nonfree
Toolchain      : Android NDK $NDK_VERSION (clang, arm64-v8a, API $API_LEVEL), static libavcodec.a + libavutil.a
Shim source    : PhotoViewer.Windows/NativeSrc/pvhevc/pvhevc.c (+ pvhevc.h)
Build script   : PhotoViewer.Android/build-native-android-hevc.sh
Built          : $(date '+%Y-%m-%d %H:%M:%S')
SHA-256        : $SO_SHA256

configure line :
$CONFIGURE_LINE
EOF
echo "  ✓ NativeLibs/arm64-v8a/pvhevc.txt"

# ── 汇总 ────────────────────────────────────────────────
echo ""
echo "══════════════════════════════════════════════════════"
echo "  完成！"
echo "    · NativeLibs/arm64-v8a/libpvhevc.so                (${SO_SIZE_KB} KB)"
echo "    · NativeLibs/arm64-v8a/pvhevc.txt"
echo "    · NativeLibs/arm64-v8a/pvhevc-FFmpeg-LICENSE.txt"
echo ""
echo "  NEEDED: $(tr '\n' ' ' <<< "$NEEDED_LIST")"
echo "  临时构建目录（可删除）：$BUILD_DIR"
echo "══════════════════════════════════════════════════════"
