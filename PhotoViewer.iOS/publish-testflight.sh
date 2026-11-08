#!/bin/bash
# 用法：bash publish-testflight.sh --edition standard|ai [--archive-only]
# 一次只为一个产品归档：standard（cc.xiaojh.PhotoViewer，无 DINO）或 ai
# （cc.xiaojh.PhotoViewer.AI，内置 DINO）。两个产品是两个独立 App Store Connect App，
# 各自持有 Bundle ID、签名资料与构建号序列，同一构建号可以用于另一产品；只拒绝
# 本产品已归档过的相同构建号（重复上传）和回退的构建号。
# --archive-only：只做本机分发签名归档与校验（archive + dSYM，不上传、不导出），
# 供本地核对签名、entitlements 与能力条件符号；不修改版本号、不创建签名资料。
# 版本号与构建号唯一来源是根 Directory.Build.props，每次上传前递增 AppBuildNumber。
set -euo pipefail

usage() {
    sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'
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

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
BUILD_PROPS="${REPO_ROOT}/Directory.Build.props"
source "${SCRIPT_DIR}/edition-identity.sh"
pv_load_edition_identity "${EDITION}" "${BUILD_PROPS}" \
    || fail "无法从 ${BUILD_PROPS} 读取 ${EDITION} 版产品身份（Bundle ID / 显示名 / 分发名 / DINO 能力）。"
DISPLAY_NAME="${PV_DISPLAY_NAME}"
PACKAGE_PRODUCT="${PV_DISTRIBUTION_NAME}"
PACKAGE_ID="${PV_PACKAGE_ID}"
DINO="${PV_ENABLE_DINO}"
VERSION="$(sed -n 's:.*<AppVersion>\(.*\)</AppVersion>.*:\1:p' "${BUILD_PROPS}" | head -n 1)"
BUILD="$(sed -n 's:.*<AppBuildNumber>\(.*\)</AppBuildNumber>.*:\1:p' "${BUILD_PROPS}" | head -n 1)"
OUTPUT_DIR="${REPO_ROOT}/release/testflight/${EDITION}"
# .xcarchive 属于打包产物，文件名用分发前缀 PhotoViewer / PhotoViewer-AI。
ARCHIVE_PATH="${OUTPUT_DIR}/${PACKAGE_PRODUCT}-${VERSION}-${BUILD}.xcarchive"
EXPORT_OPTIONS="${SCRIPT_DIR}/ExportOptions.AppStore.Local.plist"
EXPORT_OPTIONS_SAMPLE="${SCRIPT_DIR}/ExportOptions.AppStore.Local.sample.plist"
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
    fail "缺少 ${EXPORT_OPTIONS}；请从同目录 .sample.plist 复制，为两个 Bundle ID 填写描述文件并填入本机 Team ID。"
fi

mkdir -p "${OUTPUT_DIR}"

# 两个产品各自独立递增构建号：同一构建号出现在另一产品是正常情况（两个 App 记录），
# 这里只保证本产品内部单调递增，并拒绝重复上传已归档的同一构建号。
if [[ "${ARCHIVE_ONLY}" -eq 0 ]]; then
    LATEST_ARCHIVE="$(find "${OUTPUT_DIR}" -maxdepth 1 -name '*.xcarchive' 2>/dev/null | sort | tail -n 1 || true)"
    if [[ -n "${LATEST_ARCHIVE}" ]]; then
        LATEST_BUILD="$(basename "${LATEST_ARCHIVE}" | sed -n 's/.*-\([0-9][0-9]*\)\.xcarchive$/\1/p')"
        [[ -n "${LATEST_BUILD}" ]] \
            || fail "无法从已有归档解析构建号：${LATEST_ARCHIVE}（命名应为 <分发前缀>-<版本>-<构建号>.xcarchive）"
        if [[ "${BUILD}" -eq "${LATEST_BUILD}" ]]; then
            fail "${EDITION} 版构建号 ${BUILD} 已归档：${LATEST_ARCHIVE}；同一 App 不能重复上传同一构建号，请先递增 AppBuildNumber。"
        fi
        if [[ "${BUILD}" -lt "${LATEST_BUILD}" ]]; then
            fail "${EDITION} 版构建号 ${BUILD} 小于已归档的 ${LATEST_BUILD}（${LATEST_ARCHIVE}）；同一 App 的构建号必须单调递增。"
        fi
    fi
fi

# 签名资料按 edition 选：ExportOptions 的 provisioningProfiles 以 Bundle ID 为键。
# 上传路径必须使用本机 plist 的双映射；--archive-only 在本地缺该映射时退回仓库内
# .sample.plist 的约定名，仍然按 Bundle ID 校验并打印来源。
PROFILE_PLIST="${EXPORT_OPTIONS}"
PROVISION_PROFILE=""
if [[ -f "${PROFILE_PLIST}" ]]; then
    PROVISION_PROFILE="$(pv_require_profile_mapping "${PROFILE_PLIST}" "${PACKAGE_ID}" "${EDITION}" 2>/dev/null || true)"
fi
if [[ -z "${PROVISION_PROFILE}" && "${ARCHIVE_ONLY}" -eq 1 && -f "${EXPORT_OPTIONS_SAMPLE}" ]]; then
    PROFILE_PLIST="${EXPORT_OPTIONS_SAMPLE}"
    PROVISION_PROFILE="$(pv_require_profile_mapping "${PROFILE_PLIST}" "${PACKAGE_ID}" "${EDITION}" 2>/dev/null || true)"
    if [[ -n "${PROVISION_PROFILE}" ]]; then
        echo "注意：本地签名资料缺少 ${EDITION} 版映射，按仓库内 .sample.plist 的描述文件约定名归档（仅 --archive-only 允许）。"
    fi
fi
if [[ -z "${PROVISION_PROFILE}" ]]; then
    echo "FAIL: ${EXPORT_OPTIONS} 的 provisioningProfiles 必须同时映射两个产品：" >&2
    echo "      cc.xiaojh.PhotoViewer → <standard 描述文件名>，cc.xiaojh.PhotoViewer.AI → <AI 描述文件名>（见同目录 .sample.plist）。" >&2
    exit 1
fi

STAGING_DIR="${OUTPUT_DIR}/.staging-${BUILD}-$$"
STAGING_ARCHIVE="${STAGING_DIR}/${PACKAGE_PRODUCT}-${VERSION}-${BUILD}.xcarchive"
rm -rf "${STAGING_DIR}"
mkdir -p "${STAGING_DIR}"

echo "=== ${DISPLAY_NAME} ${VERSION}（${BUILD}，${PACKAGE_ID}，DINO=${DINO}）TestFlight 归档 ==="
echo "描述文件：${PROVISION_PROFILE}（来源 ${PROFILE_PLIST}）"
dotnet publish "${SCRIPT_DIR}/PhotoViewer.iOS.csproj" \
    -c Release \
    -r ios-arm64 \
    -p:PhotoViewerEdition="${EDITION}" \
    -p:ArchiveOnBuild=true \
    -p:ArchiveDir="${STAGING_ARCHIVE}" \
    -p:CodesignKey="Apple Distribution" \
    -p:CodesignProvision="${PROVISION_PROFILE}" \
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
