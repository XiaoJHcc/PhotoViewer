#!/usr/bin/env bash
# Mac/Linux 版 Android APK 发布脚本，等价于 publish-android-apk.ps1。
# 用法：bash PhotoViewer.Android/publish-android-apk.sh
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
ANDROID_PROJECT="$SCRIPT_DIR/PhotoViewer.Android.csproj"
OUTPUT_DIR="$SCRIPT_DIR/bin/Release/net10.0-android/publish"
RELEASE_DIR="$REPO_ROOT/release"
SIGNING_JSON="$SCRIPT_DIR/signing.json"

# ──────────────────────────────────────────────────────────────
# 1. 读取版本号
# ──────────────────────────────────────────────────────────────
APP_VERSION=$(sed -n 's:.*<AppVersion>\(.*\)</AppVersion>.*:\1:p' "$REPO_ROOT/Directory.Build.props" | head -n 1)
if [[ -z "$APP_VERSION" ]]; then
    echo "错误：未能从 Directory.Build.props 读取 AppVersion。" >&2; exit 1
fi

# ──────────────────────────────────────────────────────────────
# 2. 定位 Android SDK
# ──────────────────────────────────────────────────────────────
if [[ -z "${ANDROID_SDK_ROOT:-}" && -z "${ANDROID_HOME:-}" ]]; then
    if [[ -d "$HOME/Library/Android/sdk" ]]; then
        export ANDROID_SDK_ROOT="$HOME/Library/Android/sdk"
    fi
fi
ANDROID_SDK="${ANDROID_SDK_ROOT:-${ANDROID_HOME:-}}"
if [[ -z "$ANDROID_SDK" ]]; then
    echo "错误：未找到 Android SDK，请设置 ANDROID_SDK_ROOT 或将 SDK 放置于 ~/Library/Android/sdk。" >&2; exit 1
fi

# ──────────────────────────────────────────────────────────────
# 3. 读取签名配置（signing.json 或环境变量）
# ──────────────────────────────────────────────────────────────
SIGNING_ARGS=()
if [[ -f "$SIGNING_JSON" ]]; then
    KEYSTORE_FILE=$(python3 -c "import json,sys; d=json.load(open('$SIGNING_JSON')); print(d['keystoreFile'])")
    KEYSTORE_PASS=$(python3 -c "import json,sys; d=json.load(open('$SIGNING_JSON')); print(d['keystorePassword'])")
    KEY_ALIAS=$(python3 -c "import json,sys; d=json.load(open('$SIGNING_JSON')); print(d['keyAlias'])")
    KEY_PASS=$(python3 -c "import json,sys; d=json.load(open('$SIGNING_JSON')); print(d['keyPassword'])")
    # 将相对路径解析为绝对路径
    if [[ "$KEYSTORE_FILE" != /* ]]; then
        KEYSTORE_FILE="$SCRIPT_DIR/$KEYSTORE_FILE"
    fi
    if [[ ! -f "$KEYSTORE_FILE" ]]; then
        echo "错误：Keystore 文件不存在：$KEYSTORE_FILE" >&2
        echo "请运行 bash PhotoViewer.Android/setup-android-signing.sh 重新生成证书。" >&2; exit 1
    fi
    SIGNING_ARGS=(
        "/p:AndroidKeyStore=true"
        "/p:AndroidSigningKeyStore=$KEYSTORE_FILE"
        "/p:AndroidSigningKeyAlias=$KEY_ALIAS"
        "/p:AndroidSigningKeyPass=$KEY_PASS"
        "/p:AndroidSigningStorePass=$KEYSTORE_PASS"
    )
elif [[ -n "${ANDROID_KEYSTORE_PATH:-}" && -n "${ANDROID_KEYSTORE_PASSWORD:-}" && \
        -n "${ANDROID_KEY_ALIAS:-}" && -n "${ANDROID_KEY_PASSWORD:-}" ]]; then
    SIGNING_ARGS=(
        "/p:AndroidKeyStore=true"
        "/p:AndroidSigningKeyStore=$ANDROID_KEYSTORE_PATH"
        "/p:AndroidSigningKeyAlias=$ANDROID_KEY_ALIAS"
        "/p:AndroidSigningKeyPass=$ANDROID_KEY_PASSWORD"
        "/p:AndroidSigningStorePass=$ANDROID_KEYSTORE_PASSWORD"
    )
else
    echo "警告：未找到签名配置，APK 将使用临时签名，无法用于正式更新。" >&2
    echo "运行 bash PhotoViewer.Android/setup-android-signing.sh 生成证书。" >&2
fi

# ──────────────────────────────────────────────────────────────
# 4. 清理旧输出
# ──────────────────────────────────────────────────────────────
rm -rf "$REPO_ROOT/PhotoViewer/obj" "$SCRIPT_DIR/obj" "$OUTPUT_DIR"

# ──────────────────────────────────────────────────────────────
# 5. dotnet publish
# ──────────────────────────────────────────────────────────────
echo "[发布] Android SDK: $ANDROID_SDK"
echo "[发布] 输出目录: $OUTPUT_DIR"
dotnet publish "$ANDROID_PROJECT" \
    -c Release \
    -f net10.0-android \
    /p:AndroidPackageFormat=apk \
    "/p:AndroidSdkDirectory=$ANDROID_SDK" \
    "${SIGNING_ARGS[@]+"${SIGNING_ARGS[@]}"}"

# ──────────────────────────────────────────────────────────────
# 6. 复制产物到 release/
# ──────────────────────────────────────────────────────────────
mkdir -p "$RELEASE_DIR"
SIGNED_APK="$OUTPUT_DIR/cc.xiaojh.PhotoViewer-Signed.apk"
UNSIGNED_APK="$OUTPUT_DIR/cc.xiaojh.PhotoViewer.apk"

if [[ -f "$UNSIGNED_APK" ]]; then
    cp "$UNSIGNED_APK" "$RELEASE_DIR/PhotoViewer-$APP_VERSION-android.apk"
    echo "[发布] 已复制到：release/PhotoViewer-$APP_VERSION-android.apk"
fi
if [[ -f "$SIGNED_APK" ]]; then
    cp "$SIGNED_APK" "$RELEASE_DIR/PhotoViewer-$APP_VERSION-android-signed.apk"
    echo "[发布] 已复制到：release/PhotoViewer-$APP_VERSION-android-signed.apk"
fi

APK_COUNT=$(find "$OUTPUT_DIR" -name "*.apk" | wc -l | tr -d ' ')
if [[ "$APK_COUNT" -eq 0 ]]; then
    echo "错误：发布完成后未找到任何 APK 文件。" >&2; exit 1
fi
echo "[发布] 完成，共 $APK_COUNT 个 APK："; find "$OUTPUT_DIR" -name "*.apk" | sort
