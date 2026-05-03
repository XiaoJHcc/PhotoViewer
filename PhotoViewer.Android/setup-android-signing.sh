#!/usr/bin/env bash
# Mac/Linux 版 Android 签名证书初始化脚本，等价于 setup-android-signing.ps1。
# 仅需在全新机器上执行一次，或需要更换证书时重新执行。
# 用法：bash PhotoViewer.Android/setup-android-signing.sh
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
KEY_ALIAS="photoviewer"
VALIDITY_DAYS=36500
KEYSTORE_REL="keystore/release.keystore"
KEYSTORE_FILE="$SCRIPT_DIR/$KEYSTORE_REL"
SIGNING_JSON="$SCRIPT_DIR/signing.json"

# ──────────────────────────────────────────────────────────────
# 1. 定位 keytool
# ──────────────────────────────────────────────────────────────
find_keytool() {
    # JAVA_HOME 优先
    if [[ -n "${JAVA_HOME:-}" && -x "$JAVA_HOME/bin/keytool" ]]; then
        echo "$JAVA_HOME/bin/keytool"; return
    fi
    # 系统 PATH
    if command -v keytool &>/dev/null; then
        command -v keytool; return
    fi
    # macOS: /usr/libexec/java_home
    local jh; jh=$(/usr/libexec/java_home 2>/dev/null || true)
    if [[ -n "$jh" && -x "$jh/bin/keytool" ]]; then
        echo "$jh/bin/keytool"; return
    fi
    # 常见 macOS JDK 安装位置
    for dir in /Library/Java/JavaVirtualMachines/*/Contents/Home \
                "$HOME/Library/Java/JavaVirtualMachines"/*/Contents/Home; do
        if [[ -x "$dir/bin/keytool" ]]; then echo "$dir/bin/keytool"; return; fi
    done
    echo ""
}

KEYTOOL=$(find_keytool)
if [[ -z "$KEYTOOL" ]]; then
    echo ""
    echo "未找到 keytool。请执行以下任一操作后重新运行本脚本："
    echo "  - 安装 JDK（推荐：https://adoptium.net 或 brew install temurin）并设置 JAVA_HOME"
    echo "  - 安装 Android Studio（内含 JDK）"
    exit 1
fi
echo "[签名配置] 使用 keytool: $KEYTOOL"

# ──────────────────────────────────────────────────────────────
# 2. 确认 Keystore 路径
# ──────────────────────────────────────────────────────────────
if [[ -f "$KEYSTORE_FILE" ]]; then
    echo "警告：Keystore 文件已存在：$KEYSTORE_FILE"
    read -rp "是否覆盖并重新生成？(y/N) " confirm
    if [[ "$confirm" != "y" && "$confirm" != "Y" ]]; then
        echo "已取消。若需重新配置密码，请直接编辑 signing.json。"; exit 0
    fi
fi
mkdir -p "$(dirname "$KEYSTORE_FILE")"

# ──────────────────────────────────────────────────────────────
# 3. 交互式收集密码
# ──────────────────────────────────────────────────────────────
echo ""
echo "=== 配置 Android 发布签名证书 ==="
echo "说明：密码长度至少 6 位，建议使用强密码并妥善备份至密码管理器。"
echo ""
read -rsp "请输入 Keystore 密码: " STORE_PASS; echo
read -rsp "请再次输入 Keystore 密码（确认）: " STORE_PASS2; echo
if [[ "$STORE_PASS" != "$STORE_PASS2" ]]; then
    echo "密码不一致，请重新运行。" >&2; exit 1
fi
if [[ ${#STORE_PASS} -lt 6 ]]; then
    echo "Keystore 密码至少需要 6 位。" >&2; exit 1
fi
read -rsp "请输入 Key 密码（可与 Keystore 密码相同）: " KEY_PASS; echo
if [[ ${#KEY_PASS} -lt 6 ]]; then
    echo "Key 密码至少需要 6 位。" >&2; exit 1
fi

# ──────────────────────────────────────────────────────────────
# 4. 生成 Keystore
# ──────────────────────────────────────────────────────────────
DNAME="CN=PhotoViewer, O=cc.xiaojh, C=CN"
echo ""
echo "[签名配置] 正在生成 Keystore..."
"$KEYTOOL" \
    -genkeypair \
    -v \
    -storetype PKCS12 \
    -keystore "$KEYSTORE_FILE" \
    -alias "$KEY_ALIAS" \
    -keyalg RSA \
    -keysize 2048 \
    -validity "$VALIDITY_DAYS" \
    -storepass "$STORE_PASS" \
    -keypass "$KEY_PASS" \
    -dname "$DNAME"

# ──────────────────────────────────────────────────────────────
# 5. 写入 signing.json
# ──────────────────────────────────────────────────────────────
python3 - <<PYEOF
import json
config = {
    "keystoreFile":     "$KEYSTORE_REL",
    "keystorePassword": "$STORE_PASS",
    "keyAlias":         "$KEY_ALIAS",
    "keyPassword":      "$KEY_PASS"
}
with open("$SIGNING_JSON", "w", encoding="utf-8") as f:
    json.dump(config, f, indent=2, ensure_ascii=False)
    f.write("\n")
PYEOF

# ──────────────────────────────────────────────────────────────
# 6. 完成提示
# ──────────────────────────────────────────────────────────────
echo ""
echo "[签名配置] Keystore 已生成：$KEYSTORE_FILE"
echo "[签名配置] 配置文件已写入：$SIGNING_JSON"
echo ""
echo "重要提示："
echo "  1. keystore/ 目录与 signing.json 已被 .gitignore 排除，不会提交到版本库。"
echo "  2. 请将 $KEYSTORE_FILE 和密码备份到密码管理器或安全存储，丢失后无法对已发布的 APK 进行更新。"
echo "  3. 下次运行 [Publish Android APK (Release)] Task 将自动使用此证书签名。"
