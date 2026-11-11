#!/usr/bin/env bash
# 形态验收截图驱动：轮询 app 容器里的截图阶段文件，每遇到新阶段名就截一张图。
# 用法：bash PhotoViewer.iOS/form-check-shots.sh <udid> <bundleId> <输出目录>
# 只截图，不构建、不安装；阶段名 done 即退出（默认 900 秒超时）。
set -uo pipefail

if [ $# -lt 3 ]; then
    echo "usage: $0 <udid> <bundleId> <output-directory>" >&2
    exit 2
fi

UDID="$1"
BUNDLE="$2"
OUT="$3"
TIMEOUT="${FORM_SHOTS_TIMEOUT:-900}"

mkdir -p "$OUT"
CONTAINER="$(xcrun simctl get_app_container "$UDID" "$BUNDLE" data 2>/dev/null)" || {
    echo "找不到 $BUNDLE 在 $UDID 上的数据容器" >&2
    exit 3
}
STAGE="$CONTAINER/Documents/PhotoViewer/native-ui-snapshot-stage.txt"
rm -f "$STAGE"

DEADLINE=$(( $(date +%s) + TIMEOUT ))
LAST=""
while [ "$(date +%s)" -lt "$DEADLINE" ]; do
    if [ -f "$STAGE" ]; then
        NAME="$(cat "$STAGE" 2>/dev/null)"
        if [ -n "$NAME" ] && [ "$NAME" != "$LAST" ]; then
            LAST="$NAME"
            if [ "$NAME" = "done" ]; then
                echo "[form-shots] done"
                exit 0
            fi
            xcrun simctl io "$UDID" screenshot "$OUT/$NAME.png" >/dev/null 2>&1 \
                && echo "[form-shots] $NAME" \
                || echo "[form-shots] $NAME 截图失败" >&2
        fi
    fi
    sleep 0.2
done

echo "[form-shots] 超时（${TIMEOUT}s），最后阶段 ${LAST:-无}" >&2
exit 4
