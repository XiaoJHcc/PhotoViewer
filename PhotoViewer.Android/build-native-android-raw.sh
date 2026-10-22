#!/usr/bin/env bash
#
# 构建 Android arm64-v8a 的 LibRaw 静态库 + pvraw C ABI 壳，产出 libpvraw.so。
#
#   PhotoViewer.Android/NativeLibs/arm64-v8a/libpvraw.so
#   PhotoViewer.Android/NativeLibs/arm64-v8a/pvraw.txt
#   PhotoViewer.Android/NativeLibs/arm64-v8a/pvraw-LibRaw-LICENSE.txt
#
# 接口：NativeSrc/pvraw/pvraw.h（RGBA8、sRGB、不旋转、A=255）。
# LibRaw 源码与中间对象放缓存目录（默认 /tmp/pvraw-cache 与 obj/pvraw）。
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_DIR="$ROOT/NativeSrc/pvraw"
OUT_DIR="$ROOT/NativeLibs/arm64-v8a"
OUT_SO="$OUT_DIR/libpvraw.so"

LIBRAW_VER="${LIBRAW_VER:-0.22.2}"
LIBRAW_URL="https://www.libraw.org/data/LibRaw-${LIBRAW_VER}.tar.gz"
LIBRAW_TGZ="LibRaw-${LIBRAW_VER}.tar.gz"
CACHE="${PVRAW_CACHE:-/tmp/pvraw-cache}"
LIBRAW_SRC="$CACHE/LibRaw-${LIBRAW_VER}"
BUILD_DIR="$ROOT/obj/pvraw/arm64-v8a"
OBJ_DIR="$BUILD_DIR/obj"
FLAGS_RSP="$BUILD_DIR/cxxflags.rsp"

ANDROID_API=24
ABI_TARGET="aarch64-linux-android${ANDROID_API}"

# ---- NDK ----
if [[ -n "${ANDROID_NDK_HOME:-}" && -d "${ANDROID_NDK_HOME}" ]]; then
  NDK="$ANDROID_NDK_HOME"
else
  SDK="${ANDROID_HOME:-$HOME/Library/Android/sdk}"
  NDK="$(ls -d "$SDK"/ndk/*/ 2>/dev/null | sort -V | tail -1)"
  NDK="${NDK%/}"
fi
[[ -n "${NDK:-}" && -d "$NDK" ]] || { echo "找不到 Android NDK（设 ANDROID_NDK_HOME）" >&2; exit 1; }

case "$(uname -s)" in
  Darwin) HOST_TAG=darwin-x86_64 ;;
  Linux)  HOST_TAG=linux-x86_64 ;;
  *) echo "不支持的宿主系统: $(uname -s)" >&2; exit 1 ;;
esac
TOOLCHAIN="$NDK/toolchains/llvm/prebuilt/$HOST_TAG"
LLVM_BIN="$TOOLCHAIN/bin"
CXX="$LLVM_BIN/clang++"
AR="$LLVM_BIN/llvm-ar"
STRIP="$LLVM_BIN/llvm-strip"
READELF="$LLVM_BIN/llvm-readelf"
[[ -x "$CXX" ]] || { echo "缺少 $CXX" >&2; exit 1; }
NDK_VER="$(sed -n 's/^Pkg.Revision *= *//p' "$NDK/source.properties" | head -1)"

JOBS="$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 4)"

# 编译参数：不定义 LIBRAW_NOTHREADS（要线程安全版语义），OpenMP 走静态运行时。
COMMON_FLAGS=(
  "--target=$ABI_TARGET"
  -O3
  -std=c++17
  -fPIC
  -fvisibility=hidden
  -fvisibility-inlines-hidden
  -fopenmp
  -w
  "-I$LIBRAW_SRC"
)

# ---- LibRaw 源码 ----
mkdir -p "$CACHE"
if [[ ! -f "$LIBRAW_SRC/Makefile.am" ]]; then
  if [[ ! -f "$CACHE/$LIBRAW_TGZ" ]]; then
    echo "下载 $LIBRAW_URL"
    curl -fsSL --retry 3 -o "$CACHE/$LIBRAW_TGZ.part" "$LIBRAW_URL"
    mv "$CACHE/$LIBRAW_TGZ.part" "$CACHE/$LIBRAW_TGZ"
  fi
  echo "解包 $LIBRAW_TGZ"
  rm -rf "$LIBRAW_SRC"
  tar xzf "$CACHE/$LIBRAW_TGZ" -C "$CACHE"
fi
for f in libraw/libraw.h src/libraw_c_api.cpp src/tables/cameralist.cpp Makefile.am LICENSE.LGPL; do
  [[ -f "$LIBRAW_SRC/$f" ]] || { echo "LibRaw 源码不完整，缺少 $f" >&2; exit 1; }
done
# 目标机型必须在支持列表里（A7C II / A6700）
grep -q "ILCE-7CM2" "$LIBRAW_SRC/src/tables/cameralist.cpp" ||
  { echo "LibRaw $LIBRAW_VER 不支持 ILCE-7CM2" >&2; exit 1; }
grep -q "ILCE-6700" "$LIBRAW_SRC/src/tables/cameralist.cpp" ||
  { echo "LibRaw $LIBRAW_VER 不支持 ILCE-6700" >&2; exit 1; }

# 源文件列表取自 Makefile.am 的 lib_libraw_a_SOURCES（不含 samples）。
LIBRAW_SRCS=()
while IFS= read -r rel; do
  [[ -n "$rel" ]] && LIBRAW_SRCS+=("$rel")
done < <(
  sed -n '/^lib_libraw_a_SOURCES[[:space:]]*=/,/^[[:space:]]*$/p' "$LIBRAW_SRC/Makefile.am" |
    tr -d '\\' | grep -oE 'src/([A-Za-z0-9_]+/)?[A-Za-z0-9_]+\.cpp' | sort -u
)
[[ "${#LIBRAW_SRCS[@]}" -gt 40 ]] || { echo "解析 LibRaw 源文件列表失败" >&2; exit 1; }

mkdir -p "$OBJ_DIR"
rm -f "$OBJ_DIR"/*.o "$OBJ_DIR"/*.a
printf '%s\n' "${COMMON_FLAGS[@]}" > "$FLAGS_RSP"

# 每个源文件一个对象，文件名用路径扁平化，便于增量重编与排错。
compile_one() {
  local rel="$1"
  local obj="$OBJ_DIR/$(echo "$rel" | tr '/' '_' | sed 's/\.cpp$/.o/')"
  "$CXX" "@$FLAGS_RSP" -c "$LIBRAW_SRC/$rel" -o "$obj"
}
export -f compile_one
export CXX LIBRAW_SRC OBJ_DIR FLAGS_RSP

echo "编译 LibRaw ${#LIBRAW_SRCS[@]} 个源文件（jobs=${JOBS}）"
printf '%s\n' "${LIBRAW_SRCS[@]}" | xargs -n1 -P"$JOBS" bash -c 'compile_one "$@"' _

echo "编译 pvraw.cpp"
"$CXX" "${COMMON_FLAGS[@]}" -c "$SRC_DIR/pvraw.cpp" -o "$OBJ_DIR/pvraw.o"

"$AR" rcs "$OBJ_DIR/libraw.a" "$OBJ_DIR"/src_*.o

echo "链接 libpvraw.so"
mkdir -p "$OUT_DIR"
LINK_FLAGS=(
  "--target=$ABI_TARGET"
  -shared
  -fopenmp
  -static-openmp
  -static-libstdc++
  "-Wl,-soname,libpvraw.so"
  "-Wl,-z,max-page-size=16384"
  "-Wl,--gc-sections"
  "-Wl,--exclude-libs,ALL"
  "-Wl,--no-undefined"
)
"$CXX" "${LINK_FLAGS[@]}" -o "$OUT_SO" "$OBJ_DIR/pvraw.o" "$OBJ_DIR/libraw.a"

# ---- 校验 ----
fail() { echo "校验失败: $*" >&2; exit 1; }

EXPECTED_SYMS="$(printf '%s\n' pv_raw_cancel pv_raw_close pv_raw_copy_rgba8 pv_raw_exposure_gain \
  pv_raw_get_info pv_raw_last_error pv_raw_open pv_raw_process pv_raw_set_output_color \
  pv_raw_thumb_count pv_raw_thumb_data pv_raw_thumb_get_info pv_raw_thumb_unpack pv_raw_version | sort -u)"
ACTUAL_SYMS="$("$READELF" --dyn-syms --wide "$OUT_SO" |
  awk '$4 ~ /^(FUNC|OBJECT)$/ && ($5 == "GLOBAL" || $5 == "WEAK") && $6 == "DEFAULT" && $7 != "UND" { print $8 }' |
  sed 's/@.*//' | sort -u)"
[[ "$ACTUAL_SYMS" == "$EXPECTED_SYMS" ]] || fail "导出符号集合不符。
--- 实际 ---
$ACTUAL_SYMS
--- 期望 ---
$EXPECTED_SYMS"

ALLOWED_NEEDED="$(printf '%s\n' libc.so libdl.so liblog.so libm.so | sort -u)"
ACTUAL_NEEDED="$("$READELF" -d "$OUT_SO" | sed -n 's/.*Shared library: \[\(.*\)\]/\1/p' | sort -u)"
EXTRA_NEEDED="$(comm -23 <(printf '%s\n' "$ACTUAL_NEEDED") <(printf '%s\n' "$ALLOWED_NEEDED"))"
[[ -z "$EXTRA_NEEDED" ]] || fail "NEEDED 超出白名单: $EXTRA_NEEDED"

BAD_ALIGN="$("$READELF" -l "$OUT_SO" | awk '/^  LOAD/ { if ($NF != "0x4000") print $0 }')"
[[ -z "$BAD_ALIGN" ]] || fail "LOAD 对齐不是 0x4000: $BAD_ALIGN"

"$STRIP" --strip-unneeded "$OUT_SO"

# ---- 产出元数据 ----
cp "$LIBRAW_SRC/LICENSE.LGPL" "$OUT_DIR/pvraw-LibRaw-LICENSE.txt"
SHA="$({ shasum -a 256 "$OUT_SO" 2>/dev/null || sha256sum "$OUT_SO"; } | awk '{print $1}')"
SIZE="$(wc -c < "$OUT_SO" | tr -d ' ')"
{
  echo "pvraw — LibRaw C ABI shim (Android arm64-v8a)"
  echo "LibRaw: $LIBRAW_VER — 双许可 LGPL-2.1 / CDDL-1.0，见 pvraw-LibRaw-LICENSE.txt"
  echo "NDK: $NDK_VER ($HOST_TAG), target $ABI_TARGET"
  echo "compile: ${COMMON_FLAGS[*]}"
  echo "link: ${LINK_FLAGS[*]}"
  echo "sources: ${#LIBRAW_SRCS[@]} 个 LibRaw 源文件 + pvraw.cpp（无 zlib/jpeg/jasper/rawspeed/lcms）"
  echo "exposure: no_auto_bright=1 + LibRaw 直方图 1% 白点，提升上限 2.5×（约 +1.3EV）；output_color 1(sRGB)/7(DCI-P3 D65)"
  echo "exported: $(printf '%s' "$ACTUAL_SYMS" | tr '\n' ' ')"
  echo "needed: $(printf '%s' "$ACTUAL_NEEDED" | tr '\n' ' ')"
  echo "size: $SIZE bytes"
  echo "sha256: $SHA"
} > "$OUT_DIR/pvraw.txt"

echo "OK: $OUT_SO ($SIZE bytes, sha256 $SHA)"
