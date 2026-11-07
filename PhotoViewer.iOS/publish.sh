#!/bin/bash
# 用法：bash publish.sh [--edition standard|ai|both] [--local] [版本号]
# 生成两个可直接安装的本地发布包（开发签名，真机直装）：
#   release/packages/PhotoViewer-<版本>-ios.ipa        （标准版，无 DINO/ONNX Runtime）
#   release/packages/PhotoViewer-AI-<版本>-ios.ipa     （AI 版，内置模型与 ONNX Runtime）
# 稳定安装源按版本分开：release/ios-stable/<standard|ai>/PhotoViewer.iOS.app。
# 本地直装 IPA 不带 IPA 顶层 Symbols/（构建产物不 strip、不改 AOT、不重签应用），
# Symbols/ 与 dSYM 作为符号材料留在 bin/<edition>/…/publish/ 下继续可用。
# --edition 默认 both；--local 只做本机验证，IPA 落 artifacts/scratch/ios-local-publish/，
# 不写 release/packages 与 release/ios-stable。版本号与构建号唯一来源是根
# Directory.Build.props；位置参数只为兼容旧命令，必须与之一致。
set -euo pipefail

usage() {
    sed -n '2,11p' "$0" | sed 's/^# \{0,1\}//'
}

EDITION_ARG="both"
LOCAL_ONLY=0
VERSION_ARG=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --local)
            LOCAL_ONLY=1
            shift
            ;;
        --edition)
            if [[ $# -lt 2 ]]; then
                echo "--edition 需要 standard、ai 或 both"
                exit 1
            fi
            EDITION_ARG="$2"
            shift 2
            ;;
        --edition=*)
            EDITION_ARG="${1#*=}"
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        --*)
            echo "未知参数：$1"
            usage >&2
            exit 1
            ;;
        *)
            if [[ -n "${VERSION_ARG}" ]]; then
                echo "只能指定一个版本号：${VERSION_ARG} / $1"
                exit 1
            fi
            VERSION_ARG="$1"
            shift
            ;;
    esac
done

case "${EDITION_ARG}" in
    standard|ai)
        EDITIONS=("${EDITION_ARG}")
        ;;
    both)
        EDITIONS=(standard ai)
        ;;
    *)
        echo "--edition 只接受 standard、ai 或 both：${EDITION_ARG}"
        exit 1
        ;;
esac

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
BUILD_PROPS="${REPO_ROOT}/Directory.Build.props"
VERSION="$(sed -n 's:.*<AppVersion>\(.*\)</AppVersion>.*:\1:p' "${BUILD_PROPS}" | head -n 1)"
BUILD_NUMBER="$(sed -n 's:.*<AppBuildNumber>\(.*\)</AppBuildNumber>.*:\1:p' "${BUILD_PROPS}" | head -n 1)"
PROJECT_PATH="${SCRIPT_DIR}/PhotoViewer.iOS.csproj"
SCANNER="${REPO_ROOT}/Tools/check-ios-package.py"
REPORT_DIR="${REPO_ROOT}/artifacts/acceptance/ios-editions"
PACKAGE_DIR="${REPO_ROOT}/release/packages"
STABLE_DIR="${REPO_ROOT}/release/ios-stable"
LOCAL_DIR="${REPO_ROOT}/artifacts/scratch/ios-local-publish"

fail() {
    echo "FAIL: $1" >&2
    exit 1
}

# 解包/重打包/校验会在系统临时目录展开整包；失败或中断时也必须清掉。
WORK_DIR=""
cleanup_work_dir() {
    if [[ -n "${WORK_DIR}" ]]; then
        rm -rf "${WORK_DIR}"
        WORK_DIR=""
    fi
}
trap cleanup_work_dir EXIT

if [[ -z "${VERSION}" || -z "${BUILD_NUMBER}" ]]; then
    fail "未能从 ${BUILD_PROPS} 读取 AppVersion / AppBuildNumber"
fi

if [[ -n "${VERSION_ARG}" && "${VERSION_ARG}" != "${VERSION}" ]]; then
    echo "版本号参数 ${VERSION_ARG} 与 ${BUILD_PROPS} 的 AppVersion ${VERSION} 不一致。"
    echo "版本号与构建号只由根 Directory.Build.props 决定（当前 ${VERSION} / ${BUILD_NUMBER}），改版本请先更新该文件。"
    exit 1
fi

[[ -f "${PROJECT_PATH}" ]] || fail "未找到工程文件：${PROJECT_PATH}"
command -v python3 >/dev/null 2>&1 || fail "包扫描需要 python3"
[[ -f "${SCANNER}" ]] || fail "缺少包扫描工具：${SCANNER}"

write_evidence() {
    local report_path="$1"
    shift
    mkdir -p "$(dirname "${report_path}")"
    python3 - "${report_path}" "$@" <<'PY'
import json
import sys

report_path, *pairs = sys.argv[1:]
data = {}
for pair in pairs:
    key, _, value = pair.partition("=")
    if key in ("ipaSizeBytes",):
        data[key] = int(value) if value else None
    else:
        data[key] = value
with open(report_path, "w", encoding="utf-8") as handle:
    json.dump(data, handle, ensure_ascii=False, indent=2)
    handle.write("\n")
PY
}

# 从 publish 出的 IPA 里移出顶层 Symbols/（只重打包，不 strip、不重签 .app），
# 符号材料原地保留在 .../publish/Symbols，dSYM 由 dotnet publish 生成在 publish/ 下。
strip_ipa_symbols() {
    local ipa="$1"
    local symbols_dir="$2"
    local work
    WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/pv-ipa-repack.XXXXXX")"
    work="${WORK_DIR}"
    ditto -x -k "${ipa}" "${work}/unpacked" || fail "无法解包 IPA：${ipa}"
    if [[ -d "${work}/unpacked/Symbols" ]]; then
        rm -rf "${symbols_dir}"
        mkdir -p "$(dirname "${symbols_dir}")"
        mv "${work}/unpacked/Symbols" "${symbols_dir}"
    fi
    ( cd "${work}/unpacked" && zip -qry "${work}/repacked.ipa" Payload ) || fail "重新打包 IPA 失败：${ipa}"
    mv -f "${work}/repacked.ipa" "${ipa}"
    cleanup_work_dir
}

# 解包 IPA 核对 Payload 与构建产物逐字节一致（主执行文件 SHA-256）、签名完整，
# 并扫描解包后的实际 .app；IPA 内不得再带顶层 Symbols/。
verify_and_scan_ipa() {
    local ipa="$1"
    local built_app="$2"
    local edition="$3"
    local product="$4"
    local report="$5"
    local work
    WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/pv-ipa-verify.XXXXXX")"
    work="${WORK_DIR}"
    ditto -x -k "${ipa}" "${work}/unpacked" || fail "无法解包本地直装 IPA：${ipa}"
    local app="${work}/unpacked/Payload/PhotoViewer.iOS.app"
    [[ -d "${app}" ]] || fail "IPA 内未找到 Payload/PhotoViewer.iOS.app：${ipa}"
    [[ ! -d "${work}/unpacked/Symbols" ]] || fail "本地直装 IPA 仍带顶层 Symbols/：${ipa}"
    local built_hash packed_hash
    built_hash="$(shasum -a 256 "${built_app}/PhotoViewer.iOS" | awk '{print $1}')"
    packed_hash="$(shasum -a 256 "${app}/PhotoViewer.iOS" | awk '{print $1}')"
    [[ "${built_hash}" == "${packed_hash}" ]] || fail "IPA 内主执行文件与构建产物不一致（应用被改写）"
    codesign --verify --deep --strict --verbose=2 "${app}" >/dev/null 2>&1 \
        || fail "本地直装 IPA 内应用签名校验失败：${app}"
    python3 "${SCANNER}" "${app}" \
        --edition "${edition}" \
        --product-name "${product}" \
        --version "${VERSION}" \
        --build-number "${BUILD_NUMBER}" \
        --output "${report}"
    cleanup_work_dir
}

for EDITION in "${EDITIONS[@]}"; do
    case "${EDITION}" in
        ai)
            PRODUCT_NAME="PhotoViewer-AI"
            DINO="true"
            ;;
        *)
            PRODUCT_NAME="PhotoViewer"
            DINO="false"
            ;;
    esac

    if [[ "${LOCAL_ONLY}" -eq 1 ]]; then
        MODE="local"
        APP_SCAN="${REPORT_DIR}/${EDITION}-local-package.json"
        IPA_SCAN="${REPORT_DIR}/${EDITION}-local-ipa-package.json"
        EVIDENCE="${REPORT_DIR}/${EDITION}-local-publish.json"
    else
        MODE="release"
        APP_SCAN="${REPORT_DIR}/${EDITION}-package.json"
        IPA_SCAN="${REPORT_DIR}/${EDITION}-ipa-package.json"
        EVIDENCE="${REPORT_DIR}/${EDITION}-publish.json"
    fi
    STARTED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    PACKAGE_NAME="${PRODUCT_NAME}-${VERSION}-ios.ipa"

    echo ""
    if [[ "${LOCAL_ONLY}" -eq 1 ]]; then
        echo "=== [${EDITION}] 本地验证构建 ${PRODUCT_NAME} ${VERSION}（${BUILD_NUMBER}，PhotoViewerEnableDino=${DINO}）—— 不写入 release ==="
    else
        echo "=== [${EDITION}] ${PRODUCT_NAME} ${VERSION}（${BUILD_NUMBER}，PhotoViewerEnableDino=${DINO}）==="
    fi

    echo ""
    echo "=== [${EDITION}] 还原 ==="
    dotnet restore "${PROJECT_PATH}" -r ios-arm64 -p:PhotoViewerEnableDino="${DINO}"

    echo ""
    echo "=== [${EDITION}] 发布（完整 dotnet publish 流程，保留 AOT 与符号）==="
    dotnet publish "${PROJECT_PATH}" -c Release -r ios-arm64 --no-restore \
        -p:PhotoViewerEnableDino="${DINO}"

    APP_PATH="${SCRIPT_DIR}/bin/${EDITION}/Release/net10.0-ios/ios-arm64/PhotoViewer.iOS.app"
    [[ -d "${APP_PATH}" ]] \
        || fail "未找到 ${EDITION} 版应用包：${APP_PATH}（发布固定使用当前 Release ios-arm64 输出，不回退其他 RID/Debug/陈旧目录）"
    APP_ROOT="$(dirname "${APP_PATH}")"
    RAW_IPA="${APP_ROOT}/publish/PhotoViewer.iOS.ipa"
    [[ -f "${RAW_IPA}" ]] \
        || fail "未找到发布 IPA：${RAW_IPA}（publish 未产出当前 Release ios-arm64 IPA）"

    echo ""
    echo "=== [${EDITION}] 应用包扫描（实际 .app）==="
    mkdir -p "${REPORT_DIR}"
    python3 "${SCANNER}" "${APP_PATH}" \
        --edition "${EDITION}" \
        --product-name "${PRODUCT_NAME}" \
        --version "${VERSION}" \
        --build-number "${BUILD_NUMBER}" \
        --output "${APP_SCAN}"

    echo ""
    echo "=== [${EDITION}] 开发签名核对（真机直装）==="
    if ! CODESIGN_VERIFY="$(codesign --verify --deep --strict --verbose=2 "${APP_PATH}" 2>&1)"; then
        printf '%s\n' "${CODESIGN_VERIFY}" >&2
        fail "开发签名校验失败：${APP_PATH}（先跑 Refresh iOS Profile 续期，或检查证书/Provisioning）"
    fi
    printf '%s\n' "${CODESIGN_VERIFY}"

    AOT_SYMBOL_COUNT="$(nm -arch arm64 -j "${APP_PATH}/PhotoViewer.iOS" 2>/dev/null | grep -c '^_load_aot_module$' || true)"
    AOT_SYMBOL_COUNT="${AOT_SYMBOL_COUNT:-0}"
    [[ "${AOT_SYMBOL_COUNT}" -gt 0 ]] \
        || fail "主执行文件缺少 _load_aot_module：AOT 启动符号被剥离，本地直装包会崩溃（保持 MtouchNoSymbolStrip=true）"

    SYMBOLS_DIR="${APP_ROOT}/publish/Symbols"
    DSYM_APP="${APP_ROOT}/publish/PhotoViewer.iOS.app.dSYM"
    DSYM_SKIA="${APP_ROOT}/publish/libSkiaSharp.framework.dSYM"
    if [[ ! -d "${DSYM_APP}" && -d "${APP_ROOT}/PhotoViewer.iOS.app.dSYM" ]]; then
        DSYM_APP="${APP_ROOT}/PhotoViewer.iOS.app.dSYM"
        DSYM_SKIA="${APP_ROOT}/libSkiaSharp.framework.dSYM"
    fi
    [[ -d "${DSYM_APP}" ]] || fail "缺少 dSYM：${APP_ROOT}/publish/PhotoViewer.iOS.app.dSYM（发布包必须保留符号材料）"

    echo ""
    echo "=== [${EDITION}] 生成本地直装 IPA（不带 IPA 顶层 Symbols/）==="
    strip_ipa_symbols "${RAW_IPA}" "${SYMBOLS_DIR}"
    if [[ -d "${SYMBOLS_DIR}" ]]; then
        echo "符号材料：${SYMBOLS_DIR}（$(find "${SYMBOLS_DIR}" -type f | wc -l | tr -d ' ') 个文件）"
    else
        echo "符号材料：IPA 未携带 Symbols/，dSYM 保留在 ${APP_ROOT}/publish/"
    fi
    echo "dSYM：${DSYM_APP}"
    if [[ -d "${DSYM_SKIA}" ]]; then
        echo "dSYM：${DSYM_SKIA}"
    fi

    verify_and_scan_ipa "${RAW_IPA}" "${APP_PATH}" "${EDITION}" "${PRODUCT_NAME}" "${IPA_SCAN}"

    IPA_SIZE="$(stat -f%z "${RAW_IPA}")"

    if [[ "${LOCAL_ONLY}" -eq 1 ]]; then
        mkdir -p "${LOCAL_DIR}"
        PACKAGE_PATH="${LOCAL_DIR}/${PACKAGE_NAME}"
        cp -f "${RAW_IPA}" "${PACKAGE_PATH}"
        echo ""
        echo "本地验证产物（不归档、不分发）：${PACKAGE_PATH}"
    else
        mkdir -p "${PACKAGE_DIR}" "${STABLE_DIR}/${EDITION}"
        PACKAGE_PATH="${PACKAGE_DIR}/${PACKAGE_NAME}"
        cp -f "${RAW_IPA}" "${PACKAGE_PATH}"
        rm -rf "${STABLE_DIR}/${EDITION}/PhotoViewer.iOS.app"
        ditto "${APP_PATH}" "${STABLE_DIR}/${EDITION}/PhotoViewer.iOS.app"
        echo ""
        echo "发布产物：${PACKAGE_PATH}"
        echo "稳定安装源：${STABLE_DIR}/${EDITION}/PhotoViewer.iOS.app"
        echo "符号材料：${APP_ROOT}/publish/（dSYM 与 Symbols/，不入 release）"
    fi
    PACKAGE_SHA256="$(shasum -a 256 "${PACKAGE_PATH}" | awk '{print $1}')"
    echo "IPA：${IPA_SIZE} bytes，SHA-256 ${PACKAGE_SHA256}"

    write_evidence "${EVIDENCE}" \
        "mode=${MODE}" \
        "edition=${EDITION}" \
        "productName=${PRODUCT_NAME}" \
        "photoViewerEnableDino=${DINO}" \
        "version=${VERSION}" \
        "buildNumber=${BUILD_NUMBER}" \
        "versionSource=Directory.Build.props" \
        "appPath=${APP_PATH}" \
        "appScan=${APP_SCAN}" \
        "ipaPath=${PACKAGE_PATH}" \
        "ipaSizeBytes=${IPA_SIZE}" \
        "ipaSha256=${PACKAGE_SHA256}" \
        "ipaScan=${IPA_SCAN}" \
        "symbolsDir=${SYMBOLS_DIR}" \
        "dsymApp=${DSYM_APP}" \
        "dsymSkia=${DSYM_SKIA}" \
        "codesignVerify=${CODESIGN_VERIFY}" \
        "startedAt=${STARTED_AT}" \
        "finishedAt=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "证据：${EVIDENCE}"
done

if [[ "${LOCAL_ONLY}" -eq 1 ]]; then
    echo ""
    echo "本地验证完成：未写入 release/packages 与 release/ios-stable。"
    exit 0
fi

echo ""
python3 "${REPO_ROOT}/Tools/check-release.py" --write
