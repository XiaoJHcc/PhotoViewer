#!/bin/bash
APP_NAME="bin/Release/net9.0-macos/osx-arm64/PhotoViewer.Mac.app"
ENTITLEMENTS="Entitlements.plist"
SIGNING_IDENTITY="___" # matches Keychain Access certificate name

find "$APP_NAME/Contents/MacOS/"|while read fname; do
    if [[ -f $fname ]]; then
        echo "[INFO] Signing $fname"
        codesign --force --timestamp --options=runtime --entitlements "$ENTITLEMENTS" --sign "$SIGNING_IDENTITY" "$fname"
    fi
done

echo "[INFO] Signing app file"
codesign --force --timestamp --options=runtime --entitlements "$ENTITLEMENTS" --sign "$SIGNING_IDENTITY" "$APP_NAME"


echo "[INFO] Verifying signature"
codesign --verify --verbose "$APP_NAME"
codesign -dvvv "$APP_NAME"