#!/bin/bash
# 用法：bash publish.sh [版本号]   示例：bash publish.sh 0.4.0
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
DEFAULT_VERSION="$(sed -n 's:.*<AppVersion>\(.*\)</AppVersion>.*:\1:p' "${REPO_ROOT}/Directory.Build.props" | head -n 1)"
VERSION="${1:-${DEFAULT_VERSION}}"
APP_NAME="PhotoViewer.Mac"
APP_PATH="${SCRIPT_DIR}/bin/Release/net9.0-macos/osx-arm64/${APP_NAME}.app"
DIST_DIR="${SCRIPT_DIR}/bin/Release/dist"
STAGING="${DIST_DIR}/_staging"
RELEASE_DIR="${REPO_ROOT}/release"
DMG_PATH="${DIST_DIR}/PhotoViewer-${VERSION}-arm64.dmg"

if [[ -z "${VERSION}" ]]; then
    echo "❌ 未能读取版本号，请显式传入参数，例如：bash publish.sh 0.4.0"
    exit 1
fi

echo "=== 构建 ==="
dotnet publish "${SCRIPT_DIR}/${APP_NAME}.csproj" -c Release -r osx-arm64

echo ""
echo "=== 打包 DMG ==="
rm -rf "${STAGING}" && mkdir -p "${STAGING}"
cp -a "${APP_PATH}" "${STAGING}/"
ln -s /Applications "${STAGING}/Applications"
[[ -f "${SCRIPT_DIR}/安装 PhotoViewer.command" ]] && cp "${SCRIPT_DIR}/安装 PhotoViewer.command" "${STAGING}/"

hdiutil create \
    -volname "PhotoViewer" \
    -srcfolder "${STAGING}" \
    -ov -format UDZO \
    -imagekey zlib-level=9 \
    "${DMG_PATH}"

rm -rf "${STAGING}"
mkdir -p "${RELEASE_DIR}"
cp -f "${DMG_PATH}" "${RELEASE_DIR}/"
echo ""
echo "✅ ${DMG_PATH}"
echo "📦 已复制到 ${RELEASE_DIR}/$(basename "${DMG_PATH}")"
