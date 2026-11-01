#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
VERSION="$(sed -n 's:.*<AppVersion>\(.*\)</AppVersion>.*:\1:p' "${REPO_ROOT}/Directory.Build.props" | head -n 1)"
BUILD="$(sed -n 's:.*<AppBuildNumber>\(.*\)</AppBuildNumber>.*:\1:p' "${REPO_ROOT}/Directory.Build.props" | head -n 1)"
OUTPUT_DIR="${REPO_ROOT}/release/testflight"
ARCHIVE_PATH="${OUTPUT_DIR}/PhotoViewer-${VERSION}-${BUILD}.xcarchive"
EXPORT_PATH="${OUTPUT_DIR}/upload-${VERSION}-${BUILD}"
EXPORT_OPTIONS="${SCRIPT_DIR}/ExportOptions.AppStore.Local.plist"

if [[ ! -f "${EXPORT_OPTIONS}" ]]; then
    echo "缺少 ${EXPORT_OPTIONS}；请从同目录 .sample.plist 复制并填写本机 Team ID。"
    exit 1
fi

mkdir -p "${OUTPUT_DIR}"
rm -rf "${ARCHIVE_PATH}" "${EXPORT_PATH}"

dotnet publish "${SCRIPT_DIR}/PhotoViewer.iOS.csproj" \
    -c Release \
    -r ios-arm64 \
    -p:ArchiveOnBuild=true \
    -p:ArchiveDir="${ARCHIVE_PATH}" \
    -p:CodesignKey="Apple Distribution" \
    -p:CodesignProvision="PhotoViewer iOS App Store" \
    -p:AutomaticProvisioning=false

bash "${SCRIPT_DIR}/validate-testflight-archive.sh" "${ARCHIVE_PATH}"

xcodebuild -exportArchive \
    -archivePath "${ARCHIVE_PATH}" \
    -exportPath "${EXPORT_PATH}" \
    -exportOptionsPlist "${EXPORT_OPTIONS}" \
    -allowProvisioningUpdates
