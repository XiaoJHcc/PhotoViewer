#!/bin/bash
# 用法：bash publish.sh [--edition standard|ai|both] [--local] [版本号]
#       bash publish.sh --staple-only [--edition standard|ai|both] [版本号]
# --edition 是唯一产品输入，默认 both；--local 只做本机 adhoc 构建 + 包扫描 + DMG 装载验证，不签名/不公证/不归档。
# 版本号唯一来源是根 Directory.Build.props 的 AppVersion；位置参数只为兼容旧命令，必须与之一致。
# 公证已通过但票据附加失败时用 --staple-only 只重试票据附加与验证。
# 显示名、包身份（CFBundleIdentifier）与分发文件名前缀统一取根 Directory.Build.props 按 edition 派生的映射
# （PhotoViewerDisplayName / PhotoViewerPackageId / PhotoViewerDistributionName），脚本内不另写产品判断。
set -euo pipefail

usage() {
    sed -n '2,6p' "$0" | sed 's/^# \{0,1\}//'
}

EDITION_ARG="both"
STAPLE_ONLY=0
LOCAL_ONLY=0
VERSION_ARG=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --staple-only)
            STAPLE_ONLY=1
            shift
            ;;
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

if [[ "${STAPLE_ONLY}" -eq 1 && "${LOCAL_ONLY}" -eq 1 ]]; then
    echo "--staple-only 与 --local 不能同时使用：本地模式不产生可附加票据的公证包。"
    exit 1
fi

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
DEFAULT_VERSION="$(sed -n 's:.*<AppVersion>\(.*\)</AppVersion>.*:\1:p' "${BUILD_PROPS}" | head -n 1)"
BUILD_NUMBER="$(sed -n 's:.*<AppBuildNumber>\(.*\)</AppBuildNumber>.*:\1:p' "${BUILD_PROPS}" | head -n 1)"
VERSION="${DEFAULT_VERSION}"
PROJECT_PATH="${SCRIPT_DIR}/PhotoViewer.Mac.csproj"
RELEASE_DIR="${REPO_ROOT}/release"
PACKAGE_DIR="${RELEASE_DIR}/packages"
REPORT_DIR="${REPO_ROOT}/artifacts/acceptance/mac-editions"
LOCAL_DMG_DIR="${REPO_ROOT}/artifacts/scratch/mac-local-dmg"
SCANNER="${REPO_ROOT}/Tools/check-mac-package.py"
NOTARY_PROFILE="${MAC_NOTARY_PROFILE:-PhotoViewer-notary}"

# 身份只消费根 Directory.Build.props 按 edition 派生的映射；用 MSBuild 求值，避免脚本复制产品判断。
load_identity() {
    local edition="$1"
    local query
    if ! query="$(dotnet msbuild "${PROJECT_PATH}" \
        -getProperty:PhotoViewerDisplayName,PhotoViewerPackageId,PhotoViewerDistributionName,PhotoViewerProductId,PhotoViewerEnableDino \
        -p:PhotoViewerEdition="${edition}" 2>/dev/null)"; then
        echo "无法求值 ${PROJECT_PATH} 的 edition 身份（${edition}）。"
        exit 1
    fi
    eval "$(python3 - "${query}" <<'PY'
import json, shlex, sys
try:
    text = sys.argv[1]
    props = json.loads(text[text.index("{"):])["Properties"]
except Exception:
    raise SystemExit(0)
for key, name in (
    ("PhotoViewerDisplayName", "DISPLAY_NAME"),
    ("PhotoViewerPackageId", "PACKAGE_ID"),
    ("PhotoViewerDistributionName", "PACKAGE_PRODUCT"),
    ("PhotoViewerProductId", "PRODUCT_ID"),
    ("PhotoViewerEnableDino", "DINO"),
):
    print(f"{name}={shlex.quote(str(props.get(key) or ''))}")
PY
)"
    if [[ -z "${DISPLAY_NAME}" || -z "${PACKAGE_ID}" || -z "${PACKAGE_PRODUCT}" ]]; then
        echo "根 Directory.Build.props 未派生 ${edition} 的产品身份（DisplayName / PackageId / DistributionName）。"
        exit 1
    fi
}

if [[ -z "${VERSION}" || -z "${BUILD_NUMBER}" ]]; then
    echo "未能从 ${BUILD_PROPS} 读取 AppVersion / AppBuildNumber。"
    exit 1
fi

if [[ -n "${VERSION_ARG}" && "${VERSION_ARG}" != "${VERSION}" ]]; then
    echo "版本号参数 ${VERSION_ARG} 与 ${BUILD_PROPS} 的 AppVersion ${VERSION} 不一致。"
    echo "版本号与构建号只由根 Directory.Build.props 决定（当前 ${VERSION} / ${BUILD_NUMBER}），改版本请先更新该文件。"
    exit 1
fi

if [[ ! -f "${PROJECT_PATH}" ]]; then
    echo "未找到工程文件：${PROJECT_PATH}"
    exit 1
fi

if ! command -v python3 >/dev/null 2>&1; then
    echo "包扫描需要 python3。"
    exit 1
fi
if [[ ! -f "${SCANNER}" ]]; then
    echo "缺少包扫描工具：${SCANNER}"
    exit 1
fi

IDENTITY=""
if [[ "${STAPLE_ONLY}" -eq 0 && "${LOCAL_ONLY}" -eq 0 ]]; then
    IDENTITY="${MAC_DEVELOPER_ID_IDENTITY:-}"
    if [[ -z "${IDENTITY}" ]]; then
        IDENTITY="$(security find-identity -v -p codesigning | sed -n 's/.*"\(Developer ID Application:[^"]*\)".*/\1/p' | head -n 1)"
    fi
    if [[ -z "${IDENTITY}" ]]; then
        echo "未找到带私钥的 Developer ID Application 证书。"
        exit 1
    fi

    echo "=== 检查发布凭据 ==="
    echo "签名身份：${IDENTITY}"
    echo "公证钥匙串配置：${NOTARY_PROFILE}"
    xcrun notarytool history --keychain-profile "${NOTARY_PROFILE}" --output-format json >/dev/null
fi

MOUNT_POINT=""
cleanup_mount() {
    if [[ -n "${MOUNT_POINT}" ]]; then
        hdiutil detach "${MOUNT_POINT}" -quiet >/dev/null 2>&1 || true
        rmdir "${MOUNT_POINT}" 2>/dev/null || true
        MOUNT_POINT=""
    fi
}
trap cleanup_mount EXIT

write_report() {
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
    if key in ("notarySubmission",):
        try:
            data[key] = json.loads(value) if value else None
        except json.JSONDecodeError:
            data[key] = {"raw": value}
    elif key in ("dmgSizeBytes",):
        data[key] = int(value) if value else None
    elif key in ("fileCount", "totalBytes"):
        data[key] = int(value) if value else None
    else:
        data[key] = value
with open(report_path, "w", encoding="utf-8") as handle:
    json.dump(data, handle, ensure_ascii=False, indent=2)
    handle.write("\n")
PY
}

scan_app() {
    local app_path="$1"
    local edition="$2"
    local display_name="$3"
    local bundle_id="$4"
    local report_path="$5"
    python3 "${SCANNER}" "${app_path}" \
        --edition "${edition}" \
        --product-name "${display_name}" \
        --bundle-id "${bundle_id}" \
        --version "${VERSION}" \
        --build-number "${BUILD_NUMBER}" \
        --output "${report_path}"
}

# 只读挂载 DMG，扫描卷内实际 .app 与发布前扫描对照，随后卸载。
verify_mounted_dmg() {
    local dmg_path="$1"
    local edition="$2"
    local display_name="$3"
    local bundle_id="$4"
    local report_path="$5"

    MOUNT_POINT="$(mktemp -d "${TMPDIR:-/tmp}/pv-dmg-mount.XXXXXX")"
    hdiutil attach "${dmg_path}" -readonly -nobrowse -noautoopen \
        -mountpoint "${MOUNT_POINT}" -quiet
    local mounted_app="${MOUNT_POINT}/${display_name}.app"
    if [[ ! -d "${mounted_app}" ]]; then
        echo "DMG 内未找到 ${display_name}.app：${mounted_app}"
        cleanup_mount
        return 1
    fi
    if ! scan_app "${mounted_app}" "${edition}" "${display_name}" "${bundle_id}" "${report_path}"; then
        echo "DMG 内应用包扫描未通过：${report_path}"
        cleanup_mount
        return 1
    fi
    # 只读挂载下额外核对签名（adhoc 包仅验证签名完整性）。
    codesign --verify --deep --strict --verbose=2 "${mounted_app}" 2>&1 | sed 's/^/  /'
    hdiutil detach "${MOUNT_POINT}" -quiet || {
        echo "无法卸载 DMG：${MOUNT_POINT}"
        return 1
    }
    rmdir "${MOUNT_POINT}" 2>/dev/null || true
    MOUNT_POINT=""
}

for EDITION in "${EDITIONS[@]}"; do
    load_identity "${EDITION}"

    OUT_DIR="${SCRIPT_DIR}/bin/${EDITION}/Release/net10.0-macos/osx-arm64"
    APP_PATH="${OUT_DIR}/${DISPLAY_NAME}.app"
    DIST_DIR="${SCRIPT_DIR}/bin/${EDITION}/Release/dist"
    STAGING="${DIST_DIR}/_staging"
    if [[ "${LOCAL_ONLY}" -eq 1 ]]; then
        PACKAGE_NAME="${PACKAGE_PRODUCT}-${VERSION}-mac-arm64-adhoc.dmg"
        DMG_DIR="${LOCAL_DMG_DIR}"
        MODE="local-adhoc"
        APP_SCAN="${REPORT_DIR}/${EDITION}-local-package.json"
        DMG_SCAN="${REPORT_DIR}/${EDITION}-local-dmg-package.json"
        EVIDENCE="${REPORT_DIR}/${EDITION}-local-publish.json"
    else
        PACKAGE_NAME="${PACKAGE_PRODUCT}-${VERSION}-mac-arm64.dmg"
        DMG_DIR="${DIST_DIR}"
        if [[ "${STAPLE_ONLY}" -eq 1 ]]; then
            MODE="staple-only"
        else
            MODE="release"
        fi
        APP_SCAN="${REPORT_DIR}/${EDITION}-package.json"
        DMG_SCAN="${REPORT_DIR}/${EDITION}-dmg-package.json"
        EVIDENCE="${REPORT_DIR}/${EDITION}-publish.json"
    fi
    if [[ "${LOCAL_ONLY}" -eq 0 ]]; then
        EVIDENCE_NOTARY_PROFILE="${NOTARY_PROFILE}"
    else
        EVIDENCE_NOTARY_PROFILE=""
    fi
    DMG_PATH="${DMG_DIR}/${PACKAGE_NAME}"
    STARTED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

    echo ""
    if [[ "${LOCAL_ONLY}" -eq 1 ]]; then
        echo "=== [${EDITION}] 本地 adhoc 构建 ${DISPLAY_NAME} ${VERSION}（${BUILD_NUMBER}，${PACKAGE_ID}）—— 不用于分发 ==="
    else
        echo "=== [${EDITION}] ${DISPLAY_NAME} ${VERSION}（${BUILD_NUMBER}，${PACKAGE_ID}，DINO=${DINO}）==="
    fi

    if [[ "${STAPLE_ONLY}" -eq 0 ]]; then
        echo ""
        echo "=== [${EDITION}] 还原 ==="
        dotnet restore "${PROJECT_PATH}" -p:PhotoViewerEdition="${EDITION}"

        echo ""
        echo "=== [${EDITION}] 构建 ==="
        rm -rf "${APP_PATH}"
        dotnet publish "${PROJECT_PATH}" -c Release -r osx-arm64 --no-restore \
            -p:PhotoViewerEdition="${EDITION}"

        if [[ ! -d "${APP_PATH}" ]]; then
            echo "未找到应用包：${APP_PATH}"
            exit 1
        fi

        if [[ "${LOCAL_ONLY}" -eq 0 ]]; then
            echo ""
            echo "=== [${EDITION}] Developer ID + Hardened Runtime 签名 ==="
            while IFS= read -r -d '' FILE_PATH; do
                if [[ "$(file -b "${FILE_PATH}")" == *"Mach-O"* ]]; then
                    codesign --force --options runtime --timestamp --sign "${IDENTITY}" "${FILE_PATH}"
                fi
            done < <(find "${APP_PATH}" -type f -print0)

            codesign --force --options runtime --timestamp \
                --entitlements "${SCRIPT_DIR}/Entitlements.plist" \
                --sign "${IDENTITY}" "${APP_PATH}"
        else
            echo ""
            echo "=== [${EDITION}] 复用构建期 adhoc 签名 ==="
        fi

        CODESIGN_VERIFY="$(codesign --verify --deep --strict --verbose=2 "${APP_PATH}" 2>&1)"
        printf '%s\n' "${CODESIGN_VERIFY}"

        echo ""
        echo "=== [${EDITION}] 包扫描 ==="
        mkdir -p "${REPORT_DIR}"
        if ! scan_app "${APP_PATH}" "${EDITION}" "${DISPLAY_NAME}" "${PACKAGE_ID}" "${APP_SCAN}"; then
            echo "包扫描未通过，终止发布：${APP_SCAN}"
            exit 1
        fi

        echo ""
        echo "=== [${EDITION}] 打包 DMG ==="
        mkdir -p "${DMG_DIR}"
        rm -rf "${STAGING}" && mkdir -p "${STAGING}"
        cp -a "${APP_PATH}" "${STAGING}/"
        ln -s /Applications "${STAGING}/Applications"

        hdiutil create \
            -volname "${DISPLAY_NAME}" \
            -srcfolder "${STAGING}" \
            -ov -format UDZO \
            -imagekey zlib-level=9 \
            "${DMG_PATH}"
        rm -rf "${STAGING}"
        if [[ "${LOCAL_ONLY}" -eq 0 ]]; then
            codesign --force --timestamp --sign "${IDENTITY}" "${DMG_PATH}"
        else
            echo "本地 adhoc 模式不签名、不公证 DMG：${DMG_PATH}"
        fi

        DMG_SIZE="$(stat -f%z "${DMG_PATH}")"
        DMG_SHA256="$(shasum -a 256 "${DMG_PATH}" | awk '{print $1}')"
        echo "DMG：${DMG_PATH}（${DMG_SIZE} bytes，SHA-256 ${DMG_SHA256}）"

        NOTARY_OUT=""
        if [[ "${LOCAL_ONLY}" -eq 1 ]]; then
            NOTARY_STATUS="not-run"
            SIGNATURE_KIND="adhoc"
        else
            SIGNATURE_KIND="developer-id"
            echo ""
            echo "=== [${EDITION}] Apple 公证 ==="
            NOTARY_OUT="$(xcrun notarytool submit "${DMG_PATH}" \
                --keychain-profile "${NOTARY_PROFILE}" \
                --wait --no-progress --output-format json)"
            printf '%s\n' "${NOTARY_OUT}"
            NOTARY_STATUS="$(printf '%s' "${NOTARY_OUT}" | python3 -c '
import json
import sys
try:
    data = json.load(sys.stdin)
except Exception:
    print("")
    raise SystemExit(0)
if isinstance(data, dict):
    print(data.get("status", ""))
elif isinstance(data, list) and data:
    print(data[0].get("status", ""))
else:
    print("")
')"
            if [[ "${NOTARY_STATUS}" != "Accepted" ]]; then
                NOTARY_ID="$(printf '%s' "${NOTARY_OUT}" | python3 -c '
import json
import sys
try:
    data = json.load(sys.stdin)
except Exception:
    print("")
    raise SystemExit(0)
entry = data[0] if isinstance(data, list) and data else data
print(entry.get("id", "") if isinstance(entry, dict) else "")
')"
                if [[ -n "${NOTARY_ID}" ]]; then
                    xcrun notarytool log "${NOTARY_ID}" --keychain-profile "${NOTARY_PROFILE}" \
                        > "${REPORT_DIR}/${EDITION}-notarization-log.json" 2>&1 || true
                    echo "公证日志：${REPORT_DIR}/${EDITION}-notarization-log.json"
                fi
                echo "公证未通过（status=${NOTARY_STATUS}），不归档：${DMG_PATH}"
                exit 1
            fi
        fi
    else
        if [[ ! -f "${DMG_PATH}" ]]; then
            DMG_PATH="${PACKAGE_DIR}/${PACKAGE_NAME}"
        fi
        if [[ ! -f "${DMG_PATH}" ]]; then
            echo "未找到已公证的 DMG：${DIST_DIR}/${PACKAGE_NAME} 或 ${PACKAGE_DIR}/${PACKAGE_NAME}"
            exit 1
        fi
        DMG_SIZE="$(stat -f%z "${DMG_PATH}")"
        DMG_SHA256="$(shasum -a 256 "${DMG_PATH}" | awk '{print $1}')"
        CODESIGN_VERIFY=""
        NOTARY_OUT=""
        NOTARY_STATUS="accepted-previously"
        SIGNATURE_KIND="developer-id"
    fi

    STAPLE_VALIDATE=""
    GATEKEEPER=""
    if [[ "${LOCAL_ONLY}" -eq 0 ]]; then
        echo ""
        echo "=== [${EDITION}] 附加并验证公证票据 ==="
        xcrun stapler staple "${DMG_PATH}"
        STAPLE_VALIDATE="$(xcrun stapler validate "${DMG_PATH}" 2>&1)" || {
            printf '%s\n' "${STAPLE_VALIDATE}"
            echo "票据校验未通过：${DMG_PATH}（可稍后运行 --staple-only 重试）"
            exit 1
        }
        printf '%s\n' "${STAPLE_VALIDATE}"
        GATEKEEPER="$(spctl -a -vvv -t open --context context:primary-signature "${DMG_PATH}" 2>&1)" || {
            printf '%s\n' "${GATEKEEPER}"
            echo "Gatekeeper 评估未通过：${DMG_PATH}"
            exit 1
        }
        printf '%s\n' "${GATEKEEPER}"
    fi

    echo ""
    echo "=== [${EDITION}] 挂载 DMG 核对实际应用包 ==="
    verify_mounted_dmg "${DMG_PATH}" "${EDITION}" "${DISPLAY_NAME}" "${PACKAGE_ID}" "${DMG_SCAN}"

    if [[ "${LOCAL_ONLY}" -eq 0 ]]; then
        mkdir -p "${PACKAGE_DIR}"
        PACKAGE_PATH="${PACKAGE_DIR}/${PACKAGE_NAME}"
        if [[ "${DMG_PATH}" != "${PACKAGE_PATH}" ]]; then
            cp -f "${DMG_PATH}" "${PACKAGE_PATH}"
        fi
        # 票据附加会改写 DMG，归档后再取大小与哈希，证据与最终分发文件一致。
        PACKAGE_SIZE="$(stat -f%z "${PACKAGE_PATH}")"
        PACKAGE_SHA256="$(shasum -a 256 "${PACKAGE_PATH}" | awk '{print $1}')"
        echo "发布产物：${PACKAGE_PATH}（${PACKAGE_SIZE} bytes，SHA-256 ${PACKAGE_SHA256}）"
    else
        PACKAGE_PATH="${DMG_PATH}"
        PACKAGE_SIZE="${DMG_SIZE}"
        PACKAGE_SHA256="${DMG_SHA256}"
        echo "本地验证产物（不归档、不分发）：${PACKAGE_PATH}"
    fi

    write_report "${EVIDENCE}" \
        "mode=${MODE}" \
        "edition=${EDITION}" \
        "productId=${PRODUCT_ID}" \
        "packageId=${PACKAGE_ID}" \
        "productName=${DISPLAY_NAME}" \
        "distributionName=${PACKAGE_PRODUCT}" \
        "packageProduct=${PACKAGE_PRODUCT}" \
        "version=${VERSION}" \
        "buildNumber=${BUILD_NUMBER}" \
        "versionSource=Directory.Build.props" \
        "identity=${IDENTITY}" \
        "signatureKind=${SIGNATURE_KIND}" \
        "notaryProfile=${EVIDENCE_NOTARY_PROFILE}" \
        "appPath=${APP_PATH}" \
        "appScan=${APP_SCAN}" \
        "codesignVerify=${CODESIGN_VERIFY}" \
        "notaryStatus=${NOTARY_STATUS}" \
        "notarySubmission=${NOTARY_OUT}" \
        "stapleValidate=${STAPLE_VALIDATE}" \
        "gatekeeperAssess=${GATEKEEPER}" \
        "dmgPath=${PACKAGE_PATH}" \
        "dmgSizeBytes=${PACKAGE_SIZE}" \
        "dmgSha256=${PACKAGE_SHA256}" \
        "mountedAppScan=${DMG_SCAN}" \
        "startedAt=${STARTED_AT}" \
        "finishedAt=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "证据：${EVIDENCE}"
done

if [[ "${LOCAL_ONLY}" -eq 1 ]]; then
    echo ""
    echo "本地 adhoc 验证完成：未签名 Developer ID、未公证、未写入 release/packages。"
    exit 0
fi

echo ""
python3 "${REPO_ROOT}/Tools/check-release.py" --write
