#!/bin/bash
# 用法：bash publish.sh [版本号]   示例：bash publish.sh 0.4.0
set -e

VERSION="${1:-0.4.0}"
APP_NAME="PhotoViewer.Mac"
APP_PATH="bin/Release/net9.0-macos/osx-arm64/${APP_NAME}.app"
DIST_DIR="bin/Release/dist"
STAGING="${DIST_DIR}/_staging"

echo "=== 构建 ==="
dotnet publish "${APP_NAME}.csproj" -c Release -r osx-arm64

echo ""
echo "=== 打包 DMG ==="
rm -rf "${STAGING}" && mkdir -p "${STAGING}"
cp -a "${APP_PATH}" "${STAGING}/"
ln -s /Applications "${STAGING}/Applications"
[[ -f "安装 PhotoViewer.command" ]] && cp "安装 PhotoViewer.command" "${STAGING}/"

hdiutil create \
    -volname "PhotoViewer" \
    -srcfolder "${STAGING}" \
    -ov -format UDZO \
    -imagekey zlib-level=9 \
    "${DIST_DIR}/PhotoViewer-${VERSION}-arm64.dmg"

rm -rf "${STAGING}"
echo ""
echo "✅ ${DIST_DIR}/PhotoViewer-${VERSION}-arm64.dmg"
