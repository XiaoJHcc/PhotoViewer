#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
DEVICE="${1:-XJH-mini7}"
LAUNCH_COUNT="${2:-5}"
VERSION="$(sed -n 's:.*<AppVersion>\(.*\)</AppVersion>.*:\1:p' "${REPO_ROOT}/Directory.Build.props" | head -n 1)"
BUILD="$(sed -n 's:.*<AppBuildNumber>\(.*\)</AppBuildNumber>.*:\1:p' "${REPO_ROOT}/Directory.Build.props" | head -n 1)"
BUNDLE_ID="cc.xiaojh.PhotoViewer"

APPS_JSON="$(xcrun devicectl device info apps --device "${DEVICE}" --search PhotoViewer --json-output -)"
APPS_JSON="${APPS_JSON}" python3 - "${VERSION}" "${BUILD}" "${BUNDLE_ID}" <<'PY'
import json
import os
import sys
version, build, bundle_id = sys.argv[1:]
report = json.loads(os.environ['APPS_JSON'])
apps = [app for app in report['result']['apps'] if app['bundleIdentifier'] == bundle_id]
if len(apps) != 1:
    raise SystemExit(f'FAIL: 设备上找不到唯一应用 {bundle_id}')
app = apps[0]
if app['version'] != version or app['bundleVersion'] != build:
    raise SystemExit(f"FAIL: 设备版本为 {app['version']} ({app['bundleVersion']})，期望 {version} ({build})")
PY

CRASHES_BEFORE="$(xcrun devicectl device info files --device "${DEVICE}" --domain-type systemCrashLogs --search PhotoViewer)"

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

CRASHES_AFTER="$(xcrun devicectl device info files --device "${DEVICE}" --domain-type systemCrashLogs --search PhotoViewer)"
if [[ "${CRASHES_AFTER}" != "${CRASHES_BEFORE}" ]]; then
    echo "FAIL: 巡检期间产生新的崩溃报告" >&2
    echo "${CRASHES_AFTER}" >&2
    exit 1
fi

echo "PASS: ${DEVICE} 上 ${VERSION} (${BUILD}) 连续 ${LAUNCH_COUNT} 次冷启动存活，未新增崩溃报告"
