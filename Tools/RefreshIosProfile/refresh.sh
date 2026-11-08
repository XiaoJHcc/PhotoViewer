#!/bin/bash
# 用法：bash Tools/RefreshIosProfile/refresh.sh [--product standard|ai|both]
#        [--profile development|distribution|both] [--dry-run]
# 缺省只续期开发描述文件（与既有 Refresh iOS Profile 任务同口径）。为两个产品分别
# 处理本机签名资料（只驱动仓库侧，不创建 App ID 或 App 记录）：
#   development  xcodebuild build 自动签名，续期开发描述文件（免费账号 7 天有效）。
#   distribution xcodebuild archive + exportArchive（app-store-connect，自动签名），
#                需要已付费的 Apple Developer Program；缺 App ID 时由 Xcode 自己报错。
# 每次构建后核对内嵌描述文件的应用 ID 与根 Directory.Build.props 派生的 Bundle ID
# 一致，并核对内存 entitlement 与 get-task-allow 口径（开发 true / 分发 false）。
# --dry-run 只打印将执行的 xcodebuild 命令与解析出的身份，不联网、不写产物。
set -euo pipefail

usage() {
    sed -n '2,11p' "$0" | sed 's/^# \{0,1\}//'
}

PRODUCT_ARG="both"
PROFILE_ARG="development"
DRY_RUN=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --product)
            [[ $# -ge 2 ]] || { echo "--product 需要 standard、ai 或 both" >&2; exit 1; }
            PRODUCT_ARG="$2"
            shift 2
            ;;
        --product=*)
            PRODUCT_ARG="${1#*=}"
            shift
            ;;
        --profile)
            [[ $# -ge 2 ]] || { echo "--profile 需要 development、distribution 或 both" >&2; exit 1; }
            PROFILE_ARG="$2"
            shift 2
            ;;
        --profile=*)
            PROFILE_ARG="${1#*=}"
            shift
            ;;
        --dry-run)
            DRY_RUN=1
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

case "${PRODUCT_ARG}" in
    standard|ai) PRODUCTS=("${PRODUCT_ARG}") ;;
    both) PRODUCTS=(standard ai) ;;
    *)
        echo "--product 只接受 standard、ai 或 both：${PRODUCT_ARG}" >&2
        exit 1
        ;;
esac

case "${PROFILE_ARG}" in
    development|distribution) PROFILES=("${PROFILE_ARG}") ;;
    both) PROFILES=(development distribution) ;;
    *)
        echo "--profile 只接受 development、distribution 或 both：${PROFILE_ARG}" >&2
        exit 1
        ;;
esac

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
BUILD_PROPS="${REPO_ROOT}/Directory.Build.props"
PROJECT="${SCRIPT_DIR}/RefreshIosProfile.xcodeproj"
SCHEME="RefreshIosProfile"
LOCAL_XCCONFIG="${SCRIPT_DIR}/Local.xcconfig"
WORK_ROOT="${REPO_ROOT}/artifacts/scratch/refresh-ios-profile"

source "${REPO_ROOT}/PhotoViewer.iOS/edition-identity.sh"

fail() {
    echo "FAIL: $1" >&2
    exit 1
}

echo "工程：${PROJECT}"
echo "身份来源：${BUILD_PROPS}"

TEAM_ID=""
if [[ -f "${LOCAL_XCCONFIG}" ]]; then
    TEAM_ID="$(sed -n 's/^[[:space:]]*DEVELOPMENT_TEAM[[:space:]]*=[[:space:]]*\([A-Za-z0-9]*\).*/\1/p' "${LOCAL_XCCONFIG}" | tail -n 1)"
elif [[ "${DRY_RUN}" -eq 0 ]]; then
    fail "缺少 ${LOCAL_XCCONFIG}：先 cp Local.xcconfig.sample Local.xcconfig 并填写 DEVELOPMENT_TEAM。"
else
    echo "注意：缺少 Local.xcconfig，演练模式不做 Team 校验。"
fi
if [[ "${DRY_RUN}" -eq 0 && ! "${TEAM_ID}" =~ ^[A-Za-z0-9]{10}$ ]]; then
    fail "${LOCAL_XCCONFIG} 的 DEVELOPMENT_TEAM 不是 10 位 Team ID（当前值无效），先填写本机 Apple Developer Team。"
fi

run_xcodebuild() {
    if [[ "${DRY_RUN}" -eq 1 ]]; then
        local arg
        for arg in "$@"; do
            if [[ "${arg}" == DEVELOPMENT_TEAM=* ]]; then
                printf '%q ' "DEVELOPMENT_TEAM=<Local.xcconfig>"
            else
                printf '%q ' "${arg}"
            fi
        done
        echo
        return 0
    fi
    "$@"
}

# 核对构建产物内嵌描述文件：应用 ID、内存 entitlement 与开发/分发口径。
verify_profile() {
    local app="$1"
    local expected_bundle="$2"
    local expected_kind="$3"
    local provision="${app}/embedded.mobileprovision"
    [[ -f "${provision}" ]] || fail "构建产物缺少 embedded.mobileprovision：${app}（签名未生效）"
    local dump name app_id get_task_allow memory
    dump="$(mktemp "${TMPDIR:-/tmp}/pv-profile.XXXXXX")"
    if ! security cms -D -i "${provision}" > "${dump}" 2>/dev/null; then
        rm -f "${dump}"
        fail "无法解析描述文件：${provision}"
    fi
    name="$(/usr/libexec/PlistBuddy -c 'Print :Name' "${dump}" 2>/dev/null || true)"
    app_id="$(/usr/libexec/PlistBuddy -c 'Print :Entitlements:application-identifier' "${dump}" 2>/dev/null || true)"
    get_task_allow="$(/usr/libexec/PlistBuddy -c 'Print :Entitlements:get-task-allow' "${dump}" 2>/dev/null || true)"
    memory="$(/usr/libexec/PlistBuddy -c 'Print :Entitlements:com.apple.developer.kernel.increased-memory-limit' "${dump}" 2>/dev/null || true)"
    rm -f "${dump}"
    [[ "${app_id}" == *".${expected_bundle}" ]] \
        || fail "描述文件应用 ID 为 ${app_id:-<空>}，期望后缀 .${expected_bundle}（${name:-<无名称>}）"
    [[ "${memory}" == "true" ]] \
        || fail "描述文件缺少 com.apple.developer.kernel.increased-memory-limit：${name:-<无名称>}"
    if [[ "${expected_kind}" == "development" ]]; then
        [[ "${get_task_allow}" == "true" ]] || fail "开发描述文件应为 get-task-allow=true：${name:-<无名称>}"
    else
        [[ "${get_task_allow}" == "false" ]] || fail "分发描述文件应为 get-task-allow=false：${name:-<无名称>}"
    fi
    echo "  描述文件：${name}（${app_id}，get-task-allow=${get_task_allow}）"
}

build_development() {
    local bundle_id="$1"
    local derived="$2"
    local app="${derived}/Build/Products/Debug-iphoneos/RefreshIosProfile.app"
    run_xcodebuild xcodebuild -project "${PROJECT}" -scheme "${SCHEME}" -configuration Debug \
        -destination "generic/platform=iOS" \
        -derivedDataPath "${derived}" \
        PV_BUNDLE_IDENTIFIER="${bundle_id}" \
        DEVELOPMENT_TEAM="${TEAM_ID}" \
        -allowProvisioningUpdates build
    if [[ "${DRY_RUN}" -eq 1 ]]; then
        return 0
    fi
    verify_profile "${app}" "${bundle_id}" development
}

build_distribution() {
    local bundle_id="$1"
    local derived="$2"
    local archive="${derived}/RefreshIosProfile.xcarchive"
    local export_dir="${derived}/export"
    local export_options="${derived}/ExportOptions.plist"
    local unpacked="${derived}/unpacked"
    run_xcodebuild xcodebuild -project "${PROJECT}" -scheme "${SCHEME}" -configuration Release \
        -destination "generic/platform=iOS" \
        -derivedDataPath "${derived}" \
        PV_BUNDLE_IDENTIFIER="${bundle_id}" \
        DEVELOPMENT_TEAM="${TEAM_ID}" \
        CODE_SIGN_IDENTITY="Apple Distribution" \
        -allowProvisioningUpdates archive -archivePath "${archive}"
    if [[ "${DRY_RUN}" -eq 1 ]]; then
        return 0
    fi
    cat > "${export_options}" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>destination</key>
	<string>export</string>
	<key>manageAppVersionAndBuildNumber</key>
	<false/>
	<key>method</key>
	<string>app-store-connect</string>
	<key>signingStyle</key>
	<string>automatic</string>
	<key>teamID</key>
	<string>${TEAM_ID}</string>
	<key>uploadSymbols</key>
	<false/>
</dict>
</plist>
PLIST
    run_xcodebuild xcodebuild -exportArchive \
        -archivePath "${archive}" \
        -exportOptionsPlist "${export_options}" \
        -exportPath "${export_dir}" \
        -allowProvisioningUpdates
    local ipa="${export_dir}/RefreshIosProfile.ipa"
    [[ -f "${ipa}" ]] || fail "导出未生成 IPA：${export_dir}"
    ditto -x -k "${ipa}" "${unpacked}" || fail "无法解包导出 IPA：${ipa}"
    verify_profile "${unpacked}/Payload/RefreshIosProfile.app" "${bundle_id}" distribution
}

mkdir -p "${WORK_ROOT}"
for product in "${PRODUCTS[@]}"; do
    pv_load_edition_identity "${product}" "${BUILD_PROPS}" \
        || fail "无法从 ${BUILD_PROPS} 读取 ${product} 版产品身份。"
    bundle_id="${PV_PACKAGE_ID}"
    echo ""
    echo "=== ${PV_DISPLAY_NAME}（${product} / ${bundle_id}）==="
    for kind in "${PROFILES[@]}"; do
        echo "--- ${kind} 资料"
        derived="${WORK_ROOT}/${product}-${kind}"
        rm -rf "${derived}"
        if [[ "${kind}" == "development" ]]; then
            build_development "${bundle_id}" "${derived}"
        else
            build_distribution "${bundle_id}" "${derived}"
        fi
        [[ "${DRY_RUN}" -eq 1 ]] || rm -rf "${derived}"
    done
done

echo ""
if [[ "${DRY_RUN}" -eq 1 ]]; then
    echo "演练结束：未执行 xcodebuild、未创建或改动任何签名资料。"
else
    echo "完成：已按产品核对开发/分发描述文件；Apple 后台的 App ID 与 App 记录仍须人工确认。"
fi
