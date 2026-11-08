#!/bin/bash
# 用法：bash publish-testflight.sh --edition standard|ai [--archive-only]
# 一次只为一个明确版本归档并上传材料：standard（无 DINO）或 ai（内置 DINO）。
# 不传 --edition、传 both 或一次构建两个版本都直接报错退出 —— App Store Connect
# 同一构建号只能对应一个变体，双变体必须分两次递增构建号后分别归档。
# --archive-only：只做本机分发签名归档与校验（archive + dSYM，不上传、不导出），
# 供本地核对签名、entitlements 与能力条件符号；不修改版本号、不创建签名资料。
# 版本号与构建号唯一来源是根 Directory.Build.props，每次上传前递增 AppBuildNumber。
set -euo pipefail

usage() {
    sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//'
}

EDITION=""
ARCHIVE_ONLY=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --edition)
            if [[ $# -lt 2 ]]; then
                echo "--edition 需要 standard 或 ai" >&2
                exit 1
            fi
            EDITION="$2"
            shift 2
            ;;
        --edition=*)
            EDITION="${1#*=}"
            shift
            ;;
        --archive-only)
            ARCHIVE_ONLY=1
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "未知参数：$1" >&2
            usage >&2
            exit 1
            ;;
    esac
done

case "${EDITION}" in
    standard|ai)
        ;;
    both)
        echo "--edition 只能指定一个版本：双变体必须分两次归档，且各自使用不同构建号（不改版本号、不拆同一 build）。" >&2
        exit 1
        ;;
    "")
        echo "必须显式指定 --edition standard 或 --edition ai：TestFlight 不允许一次构建两个变体。" >&2
        usage >&2
        exit 1
        ;;
    *)
        echo "--edition 只接受 standard 或 ai：${EDITION}" >&2
        exit 1
        ;;
esac

case "${EDITION}" in
    ai)
        DISPLAY_NAME="PhotoViewer AI"
        PACKAGE_PRODUCT="PhotoViewer-AI"
        DINO="true"
        ;;
    *)
        DISPLAY_NAME="PhotoViewer"
        PACKAGE_PRODUCT="PhotoViewer"
        DINO="false"
        ;;
esac

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
VERSION="$(sed -n 's:.*<AppVersion>\(.*\)</AppVersion>.*:\1:p' "${REPO_ROOT}/Directory.Build.props" | head -n 1)"
BUILD="$(sed -n 's:.*<AppBuildNumber>\(.*\)</AppBuildNumber>.*:\1:p' "${REPO_ROOT}/Directory.Build.props" | head -n 1)"
OUTPUT_DIR="${REPO_ROOT}/release/testflight/${EDITION}"
# .xcarchive 属于打包产物，文件名继续用 PhotoViewer-AI。
ARCHIVE_PATH="${OUTPUT_DIR}/${PACKAGE_PRODUCT}-${VERSION}-${BUILD}.xcarchive"
EXPORT_OPTIONS="${SCRIPT_DIR}/ExportOptions.AppStore.Local.plist"
EXPORT_DIR="${REPO_ROOT}/artifacts/scratch/ios-testflight-export/${EDITION}-${VERSION}-${BUILD}"

fail() {
    echo "FAIL: $1" >&2
    exit 1
}

# 新归档先构建到 <edition>/.staging-<build> 下的同名 .xcarchive（保留 validate 的命名契约），
# 校验通过后才置换正式路径；失败/中断时清掉本次临时产物，旧归档原样保留。
STAGING_DIR=""
cleanup_staging() {
    if [[ -n "${STAGING_DIR}" && -d "${STAGING_DIR}" ]]; then
        if [[ -d "${STAGING_DIR}/previous.xcarchive" && ! -e "${ARCHIVE_PATH}" ]]; then
            if ! mv "${STAGING_DIR}/previous.xcarchive" "${ARCHIVE_PATH}"; then
                echo "旧归档无法自动恢复，已保留回滚材料：${STAGING_DIR}/previous.xcarchive" >&2
                return
            fi
        fi
        rm -rf "${STAGING_DIR}"
        STAGING_DIR=""
    fi
}
trap cleanup_staging EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

[[ -n "${VERSION}" && -n "${BUILD}" ]] || fail "未能从 Directory.Build.props 读取 AppVersion / AppBuildNumber"
if [[ "${ARCHIVE_ONLY}" -eq 0 && ! -f "${EXPORT_OPTIONS}" ]]; then
    fail "缺少 ${EXPORT_OPTIONS}；请从同目录 .sample.plist 复制并填写本机 Team ID。"
fi

mkdir -p "${OUTPUT_DIR}"

if [[ "${ARCHIVE_ONLY}" -eq 0 ]]; then
    if [[ "${EDITION}" == "standard" ]]; then
        OTHER_EDITION="ai"
    else
        OTHER_EDITION="standard"
    fi
    PREVIOUS_SAME_BUILD="$(find "${REPO_ROOT}/release/testflight/${OTHER_EDITION}" -maxdepth 1 \
        -name "*-${VERSION}-${BUILD}.xcarchive" 2>/dev/null | head -n 1 || true)"
    if [[ -n "${PREVIOUS_SAME_BUILD}" ]]; then
        fail "构建号 ${BUILD} 已用于 ${OTHER_EDITION} 版归档：${PREVIOUS_SAME_BUILD}；同一构建号不能对应两个变体，请先递增 AppBuildNumber。"
    fi
fi

STAGING_DIR="${OUTPUT_DIR}/.staging-${BUILD}-$$"
STAGING_ARCHIVE="${STAGING_DIR}/${PACKAGE_PRODUCT}-${VERSION}-${BUILD}.xcarchive"
rm -rf "${STAGING_DIR}"
mkdir -p "${STAGING_DIR}"

echo "=== ${DISPLAY_NAME} ${VERSION}（${BUILD}，PhotoViewerEnableDino=${DINO}）TestFlight 归档 ==="
dotnet publish "${SCRIPT_DIR}/PhotoViewer.iOS.csproj" \
    -c Release \
    -r ios-arm64 \
    -p:PhotoViewerEnableDino="${DINO}" \
    -p:ArchiveOnBuild=true \
    -p:ArchiveDir="${STAGING_ARCHIVE}" \
    -p:CodesignKey="Apple Distribution" \
    -p:CodesignProvision="PhotoViewer iOS App Store" \
    -p:AutomaticProvisioning=false

bash "${SCRIPT_DIR}/validate-testflight-archive.sh" "${STAGING_ARCHIVE}" --edition "${EDITION}"

# 校验通过后才置换正式归档：旧归档先挪进临时目录作为回滚点，新归档落位后一并清理。
if [[ -d "${ARCHIVE_PATH}" ]]; then
    rm -rf "${STAGING_DIR}/previous.xcarchive"
    mv "${ARCHIVE_PATH}" "${STAGING_DIR}/previous.xcarchive"
fi
if ! mv "${STAGING_ARCHIVE}" "${ARCHIVE_PATH}"; then
    if [[ -d "${STAGING_DIR}/previous.xcarchive" ]]; then
        mv "${STAGING_DIR}/previous.xcarchive" "${ARCHIVE_PATH}"
    fi
    fail "无法把新归档置换到正式路径：${ARCHIVE_PATH}（已尝试恢复旧归档）"
fi
rm -rf "${STAGING_DIR}"
STAGING_DIR=""

# 归档按版本各自保留最新一份；另一变体的归档不在本目录，互不覆盖。
find "${OUTPUT_DIR}" -mindepth 1 -maxdepth 1 -name '*.xcarchive' ! -name "$(basename "${ARCHIVE_PATH}")" -exec rm -rf {} +
find "${OUTPUT_DIR}" -mindepth 1 -maxdepth 1 -name 'upload-*' -exec rm -rf {} +

# 归档成功后收敛旧布局（顶层单归档 / 导出目录），双变体材料各自留在 <edition>/ 下。
while IFS= read -r -d '' entry; do
    echo "移除旧布局归档/导出目录：${entry}"
    rm -rf "${entry}"
done < <(find "${REPO_ROOT}/release/testflight" -mindepth 1 -maxdepth 1 \
    \( -name '*.xcarchive' -o -name 'upload-*' \) -print0)

echo ""
echo "归档：${ARCHIVE_PATH}"
echo "符号材料：${ARCHIVE_PATH}/dSYMs（TestFlight 上传保留符号化）"

if [[ "${ARCHIVE_ONLY}" -eq 1 ]]; then
    echo "已按 --archive-only 结束：未导出、未上传，未改动版本号与构建号。"
    exit 0
fi

echo ""
echo "=== 导出分发材料 ==="
DESTINATION="$(/usr/libexec/PlistBuddy -c 'Print :destination' "${EXPORT_OPTIONS}" 2>/dev/null || echo "")"
rm -rf "${EXPORT_DIR}"
mkdir -p "${EXPORT_DIR}"
xcodebuild -exportArchive \
    -archivePath "${ARCHIVE_PATH}" \
    -exportOptionsPlist "${EXPORT_OPTIONS}" \
    -exportPath "${EXPORT_DIR}" \
    -allowProvisioningUpdates

if [[ "${DESTINATION}" == "upload" ]]; then
    echo ""
    echo "已按 ${EXPORT_OPTIONS} 的 destination=upload 上传 App Store Connect（使用本机 Xcode 已登录账号）。"
    echo "归档：${ARCHIVE_PATH}（dSYMs 随上传保留符号化）"
else
    IPA_PATH="${EXPORT_DIR}/PhotoViewer.iOS.ipa"
    [[ -f "${IPA_PATH}" ]] || fail "导出未生成 IPA：${EXPORT_DIR}"
    echo ""
    echo "导出：${IPA_PATH}"
    echo "上传由本机账号完成；本脚本不改写版本号、构建号或签名资料。"
fi
