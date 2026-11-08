#!/usr/bin/env bash
# Mac/Linux 版 Android APK 发布脚本，等价于 publish-android-apk.ps1。
# 用法：bash PhotoViewer.Android/publish-android-apk.sh [--edition standard|ai|both] [--local]
#
# 两个发行版是并列产品，同机并存、各自沙盒（包身份唯一来源：根 Directory.Build.props）：
#   standard  PhotoViewer     cc.xiaojh.PhotoViewer      无 DINO 模型 / ONNX Runtime / 许可证
#   ai        PhotoViewer AI  cc.xiaojh.PhotoViewer.AI   内置 DINOv3 模型、ONNX Runtime 与许可证
# 显示名（android:label）用空格；分发包文件名用横线（PhotoViewer-AI）。
# 只发 arm64-v8a，APK 不得含 x86_64 / armeabi-v7a。
#
# 正式发布写 release/packages/<PhotoViewer|PhotoViewer-AI>-<AppVersion>-android-arm64.apk；
# --local 只做本机验证，产物落 artifacts/scratch/android-local-publish/，不写 release/。
# --edition 默认 both。版本号、构建号与产品身份唯一来源是根 Directory.Build.props。
# 构建与还原都显式传 -p:PhotoViewerEdition=standard|ai，两版 bin/obj 按 edition 隔离。
# 自动选用已安装且真正具备 android workload 的 .NET SDK（仓库 global.json 不做修改）。
set -euo pipefail

usage() {
    sed -n '2,14p' "$0" | sed 's/^# \{0,1\}//'
}

EDITION_ARG="both"
LOCAL_ONLY=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --local)
            LOCAL_ONLY=1
            shift
            ;;
        --edition)
            if [[ $# -lt 2 ]]; then
                echo "--edition 需要 standard、ai 或 both" >&2
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
            echo "未知参数：$1" >&2
            usage >&2
            exit 1
            ;;
        *)
            echo "未知位置参数：$1（版本号只由 Directory.Build.props 决定）" >&2
            usage >&2
            exit 1
            ;;
    esac
done

case "${EDITION_ARG}" in
    standard|ai)
        EDITIONS=("${EDITION_ARG}")
        ;;
    both)
        EDITIONS=(standard ai)
        ;;
    *)
        echo "--edition 只接受 standard、ai 或 both：${EDITION_ARG}" >&2
        exit 1
        ;;
esac

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
ANDROID_PROJECT="$SCRIPT_DIR/PhotoViewer.Android.csproj"
SCANNER="$REPO_ROOT/Tools/check-android-package.py"
RELEASE_DIR="$REPO_ROOT/release/packages"
LOCAL_DIR="$REPO_ROOT/artifacts/scratch/android-local-publish"
REPORT_DIR="$REPO_ROOT/artifacts/acceptance/android-editions"
SIGNING_JSON="$SCRIPT_DIR/signing.json"

fail() {
    echo "FAIL: $1" >&2
    exit 1
}

[[ -f "$ANDROID_PROJECT" ]] || fail "未找到工程文件：$ANDROID_PROJECT"
command -v dotnet >/dev/null 2>&1 || fail "未找到 dotnet"
command -v python3 >/dev/null 2>&1 || fail "Android 包扫描需要 python3"
[[ -f "$SCANNER" ]] || fail "缺少包扫描工具：$SCANNER"

# ──────────────────────────────────────────────────────────────
# 1. 读取版本号，并按 edition 读取根 props 的产品身份映射
# ──────────────────────────────────────────────────────────────
APP_VERSION=$(sed -n 's:.*<AppVersion>\(.*\)</AppVersion>.*:\1:p' "$REPO_ROOT/Directory.Build.props" | head -n 1)
if [[ -z "$APP_VERSION" ]]; then
    fail "未能从 Directory.Build.props 读取 AppVersion。"
fi

# edition 的包名、显示名、分发前缀与 DINO 能力都来自同一个 PropertyGroup，脚本不另写产品判断。
read_edition_identity() {
    python3 - "$REPO_ROOT/Directory.Build.props" "$1" <<'PY'
import re
import sys

path, edition = sys.argv[1], sys.argv[2]
with open(path, encoding="utf-8-sig") as handle:
    text = handle.read()
for key in ("PhotoViewerPackageId", "PhotoViewerDisplayName",
            "PhotoViewerDistributionName", "PhotoViewerEnableDino"):
    pattern = re.compile(
        rf"<{key}\s+Condition=\"'\$\(PhotoViewerEdition\)'\s*==\s*'"
        rf"{re.escape(edition)}'\">([^<]+)</{key}>")
    match = pattern.search(text)
    print(match.group(1).strip() if match else "")
PY
}

# ──────────────────────────────────────────────────────────────
# 2. 定位 Android SDK
# ──────────────────────────────────────────────────────────────
if [[ -z "${ANDROID_SDK_ROOT:-}" && -z "${ANDROID_HOME:-}" ]]; then
    for candidate in "$HOME/Library/Android/sdk" "$HOME/Android/Sdk"; do
        if [[ -d "$candidate" ]]; then
            export ANDROID_SDK_ROOT="$candidate"
            break
        fi
    done
fi
ANDROID_SDK="${ANDROID_SDK_ROOT:-${ANDROID_HOME:-}}"
if [[ -z "$ANDROID_SDK" ]]; then
    fail "未找到 Android SDK，请设置 ANDROID_SDK_ROOT，或将 SDK 放在 ~/Library/Android/sdk。"
fi

# ──────────────────────────────────────────────────────────────
# 3. 选择真正具备 android workload 的 .NET SDK
#    每个 SDK 版本的工作负载互相独立：global.json 解析到的版本未必装了 android。
#    优先用当前目录解析到的 SDK；缺该 workload 时逐个用临时 global.json 探测
#    dotnet --list-sdks 里的已安装版本，取最高可用者，并在该目录里执行发布。
#    仓库 global.json 不做任何修改。
# ──────────────────────────────────────────────────────────────
SDK_PROBE_ROOT=""
cleanup_sdk_probes() {
    if [[ -n "$SDK_PROBE_ROOT" && -d "$SDK_PROBE_ROOT" ]]; then
        rm -rf "$SDK_PROBE_ROOT"
    fi
}
trap cleanup_sdk_probes EXIT

has_android_workload() {
    local directory="$1" output line
    output=$( cd "$directory" && dotnet workload list 2>/dev/null ) || return 1
    while IFS= read -r line; do
        if [[ "$line" =~ ^[[:space:]]*android[[:space:]] ]]; then
            return 0
        fi
    done <<< "$output"
    return 1
}

PUBLISH_WORK_DIR="$PWD"
if has_android_workload "$PUBLISH_WORK_DIR"; then
    echo "[发布] 当前 SDK $(dotnet --version) 已具备 android workload"
else
    echo "[发布] 当前 SDK 缺少 android workload，改用已安装且具备该 workload 的 SDK。" >&2
    probe_base="${TMPDIR:-/tmp}"
    probe_base="${probe_base%/}"
    SDK_PROBE_ROOT=$(mktemp -d "${probe_base}/pv-android-sdk.XXXXXX")
    selected_dir=""
    selected_version=""
    while IFS= read -r version; do
        [[ -n "$version" ]] || continue
        probe_dir="$SDK_PROBE_ROOT/$version"
        mkdir -p "$probe_dir"
        printf '{"sdk":{"version":"%s","rollForward":"disable"}}\n' "$version" > "$probe_dir/global.json"
        # --list-sdks 按版本升序，命中后继续往后找，最终留下最高可用版本。
        if has_android_workload "$probe_dir"; then
            selected_dir="$probe_dir"
            selected_version="$version"
        fi
    done < <(dotnet --list-sdks | awk 'NF >= 2 { print $1 }')
    if [[ -z "$selected_dir" ]]; then
        fail "未找到安装了 android workload 的 .NET SDK（dotnet --list-sdks 无可用版本）。"
    fi
    PUBLISH_WORK_DIR="$selected_dir"
    echo "[发布] 使用 SDK ${selected_version}（$( cd "$PUBLISH_WORK_DIR" && dotnet --version )），发布工作目录：${PUBLISH_WORK_DIR}"
fi

# ──────────────────────────────────────────────────────────────
# 4. 读取签名配置（signing.json 或环境变量）；凭据只进命令行参数，不打印
# ──────────────────────────────────────────────────────────────
SIGNING_ARGS=()
if [[ -f "$SIGNING_JSON" ]]; then
    SIGNING_VALUES=$(python3 - "$SIGNING_JSON" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8-sig") as handle:
    data = json.load(handle)
for key in ("keystoreFile", "keystorePassword", "keyAlias", "keyPassword"):
    print(data[key])
PY
)
    KEYSTORE_FILE=$(printf '%s\n' "$SIGNING_VALUES" | sed -n '1p')
    KEYSTORE_PASS=$(printf '%s\n' "$SIGNING_VALUES" | sed -n '2p')
    KEY_ALIAS=$(printf '%s\n' "$SIGNING_VALUES" | sed -n '3p')
    KEY_PASS=$(printf '%s\n' "$SIGNING_VALUES" | sed -n '4p')
    if [[ "$KEYSTORE_FILE" != /* ]]; then
        KEYSTORE_FILE="$SCRIPT_DIR/$KEYSTORE_FILE"
    fi
    if [[ ! -f "$KEYSTORE_FILE" ]]; then
        fail "Keystore 文件不存在：${KEYSTORE_FILE}（运行 bash PhotoViewer.Android/setup-android-signing.sh 重新生成）"
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

echo "[发布] Android SDK: $ANDROID_SDK"
echo "[发布] 发行版本: ${EDITIONS[*]}"
if [[ $LOCAL_ONLY -eq 1 ]]; then
    echo "[发布] 本地验证模式：产物写入 artifacts/scratch/android-local-publish，不写 release/"
fi

# ──────────────────────────────────────────────────────────────
# 5. 逐版发布：身份按 edition 从根 props 读取；只清当前 edition 的发布输出
# ──────────────────────────────────────────────────────────────
for edition in "${EDITIONS[@]}"; do
    IDENTITY=$(read_edition_identity "$edition")
    PACKAGE_ID=$(printf '%s\n' "$IDENTITY" | sed -n '1p')
    DISPLAY_NAME=$(printf '%s\n' "$IDENTITY" | sed -n '2p')
    DISTRIBUTION_NAME=$(printf '%s\n' "$IDENTITY" | sed -n '3p')
    ENABLE_DINO=$(printf '%s\n' "$IDENTITY" | sed -n '4p')
    if [[ -z "$PACKAGE_ID" || -z "$DISPLAY_NAME" || -z "$DISTRIBUTION_NAME" \
        || ( "$ENABLE_DINO" != "true" && "$ENABLE_DINO" != "false" ) ]]; then
        fail "根 Directory.Build.props 未给出 ${edition} 的完整产品身份（包名/显示名/分发前缀/DINO 能力）。"
    fi
    publish_dir="$SCRIPT_DIR/bin/$edition/Release/net10.0-android/publish"

    echo "[发布] === ${edition}（${DISPLAY_NAME}，${PACKAGE_ID}） ==="
    rm -rf "$publish_dir"

    (
        cd "$PUBLISH_WORK_DIR"
        dotnet publish "$ANDROID_PROJECT" \
            -c Release \
            -f net10.0-android \
            "/p:PhotoViewerEdition=$edition" \
            /p:AndroidPackageFormat=apk \
            "/p:AndroidSdkDirectory=$ANDROID_SDK" \
            "${SIGNING_ARGS[@]+"${SIGNING_ARGS[@]}"}"
    )

    apk=""
    for candidate in "$publish_dir/$PACKAGE_ID-Signed.apk" "$publish_dir/$PACKAGE_ID.apk"; do
        if [[ -f "$candidate" ]]; then
            apk="$candidate"
            break
        fi
    done
    if [[ -z "$apk" && -d "$publish_dir" ]]; then
        apk=$(find "$publish_dir" -maxdepth 1 -name '*.apk' -type f -print -quit)
    fi
    if [[ -z "$apk" || ! -f "$apk" ]]; then
        fail "$edition 发布完成后未找到 APK：$publish_dir"
    fi

    if [[ $LOCAL_ONLY -eq 1 ]]; then
        dest_dir="$LOCAL_DIR"
    else
        dest_dir="$RELEASE_DIR"
    fi
    mkdir -p "$dest_dir" "$REPORT_DIR"
    dest_apk="$dest_dir/$DISTRIBUTION_NAME-$APP_VERSION-android-arm64.apk"
    cp "$apk" "$dest_apk"
    echo "[发布] 产物：$dest_apk"

    # 把 edition 对应的期望身份显式交给扫描器：实际 APK 与期望值都写进报告。
    python3 "$SCANNER" "$dest_apk" \
        --edition "$edition" \
        --package-id "$PACKAGE_ID" \
        --display-name "$DISPLAY_NAME" \
        --output "$REPORT_DIR/$edition-package.json"
done

# ──────────────────────────────────────────────────────────────
# 6. 正式发布后刷新 release/SHA256SUMS；本地验证不动 release/
# ──────────────────────────────────────────────────────────────
if [[ $LOCAL_ONLY -eq 0 ]]; then
    python3 "$REPO_ROOT/Tools/check-release.py" --write
fi

echo "[发布] 完成：${EDITIONS[*]}"
