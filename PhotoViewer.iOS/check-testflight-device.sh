#!/bin/bash
# 用法：bash check-testflight-device.sh [设备名或UDID] [冷启动次数] [--edition standard|ai]
# 按 edition 核对设备上已安装产品的 Bundle ID、显示名与版本号/构建号
# （standard → cc.xiaojh.PhotoViewer / PhotoViewer，ai → cc.xiaojh.PhotoViewer.AI /
# PhotoViewer AI），随后连续冷启动若干次并确认进程存活、没有新增崩溃报告。
# 省略 --edition 时按 standard 核对并打印实际使用的身份。设备需先通过 TestFlight
# 或本地直装安装当前构建。
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
BUILD_PROPS="${REPO_ROOT}/Directory.Build.props"
source "${SCRIPT_DIR}/edition-identity.sh"

DEVICE=""
LAUNCH_COUNT="5"
EDITION=""
POSITIONAL=0
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
        -h|--help)
            sed -n '2,7p' "$0" | sed 's/^# \{0,1\}//'
            exit 0
            ;;
        --*)
            echo "未知参数：$1" >&2
            exit 1
            ;;
        *)
            POSITIONAL=$((POSITIONAL + 1))
            if [[ "${POSITIONAL}" -eq 1 ]]; then
                DEVICE="$1"
            elif [[ "${POSITIONAL}" -eq 2 ]]; then
                LAUNCH_COUNT="$1"
            else
                echo "多余参数：$1" >&2
                exit 1
            fi
            shift
            ;;
    esac
done

EDITION="${EDITION:-standard}"
pv_load_edition_identity "${EDITION}" "${BUILD_PROPS}" \
    || { echo "FAIL: 无法从 ${BUILD_PROPS} 读取 ${EDITION} 版产品身份" >&2; exit 1; }
BUNDLE_ID="${PV_PACKAGE_ID}"
EXPECTED_DISPLAY="${PV_DISPLAY_NAME}"
echo "核对身份：${EDITION} / ${BUNDLE_ID} / ${EXPECTED_DISPLAY}（ProductId ${PV_PRODUCT_ID}）"

DEVICE="${DEVICE:-XJH-mini7}"
VERSION="$(sed -n 's:.*<AppVersion>\(.*\)</AppVersion>.*:\1:p' "${BUILD_PROPS}" | head -n 1)"
BUILD="$(sed -n 's:.*<AppBuildNumber>\(.*\)</AppBuildNumber>.*:\1:p' "${BUILD_PROPS}" | head -n 1)"

APPS_JSON="$(xcrun devicectl device info apps --device "${DEVICE}" --search PhotoViewer --json-output -)"
APPS_JSON="${APPS_JSON}" python3 - "${VERSION}" "${BUILD}" "${BUNDLE_ID}" "${EXPECTED_DISPLAY}" "${EDITION}" <<'PY'
import json
import os
import sys

version, build, bundle_id, expected_display, edition = sys.argv[1:]
report = json.loads(os.environ['APPS_JSON'])
apps = [app for app in report['result']['apps'] if app['bundleIdentifier'] == bundle_id]
if len(apps) != 1:
    installed = sorted({app.get('bundleIdentifier') for app in report['result']['apps']})
    raise SystemExit(
        f'FAIL: 设备上找不到唯一应用 {bundle_id}（{edition}）；已安装：{"、".join(filter(None, installed)) or "无"}'
    )
app = apps[0]
if app['version'] != version or app['bundleVersion'] != build:
    raise SystemExit(f"FAIL: 设备版本为 {app['version']} ({app['bundleVersion']})，期望 {version} ({build})")

display = next(
    (app[key] for key in ('displayName', 'bundleDisplayName', 'name') if app.get(key)),
    None,
)
if display is None:
    raise SystemExit(f'FAIL: devicectl 未返回应用显示名，无法核对 {edition} 版 {bundle_id}')
if display != expected_display:
    raise SystemExit(
        f'FAIL: 设备应用显示名为 {display}，期望 {expected_display}（{edition} / {bundle_id}）'
    )
PY

CRASHES_BEFORE="$(xcrun devicectl device info files --device "${DEVICE}" --domain-type systemCrashLogs --search "${EXPECTED_DISPLAY}")"

for ((index = 1; index <= LAUNCH_COUNT; index++)); do
    echo "冷启动 ${index}/${LAUNCH_COUNT}"
    xcrun devicectl device process launch --device "${DEVICE}" --terminate-existing "${BUNDLE_ID}" >/dev/null
    sleep 2
    PROCESSES_JSON="$(xcrun devicectl device info processes --device "${DEVICE}" --search PhotoViewer --json-output -)"
    PROCESSES_JSON="${PROCESSES_JSON}" python3 - <<'PY'
import json
import os
report = json.loads(os.environ['PROCESSES_JSON'])
processes = report['result']['runningProcesses']
if not any(process['executable'].endswith('/PhotoViewer.iOS.app/PhotoViewer.iOS') for process in processes):
    raise SystemExit('FAIL: 启动后进程未存活')
PY
done

CRASHES_AFTER="$(xcrun devicectl device info files --device "${DEVICE}" --domain-type systemCrashLogs --search "${EXPECTED_DISPLAY}")"
if [[ "${CRASHES_AFTER}" != "${CRASHES_BEFORE}" ]]; then
    echo "FAIL: 巡检期间产生新的崩溃报告" >&2
    echo "${CRASHES_AFTER}" >&2
    exit 1
fi

echo "PASS: ${DEVICE} 上 ${EXPECTED_DISPLAY}（${BUNDLE_ID}）${VERSION} (${BUILD}) 连续 ${LAUNCH_COUNT} 次冷启动存活，未新增崩溃报告"
