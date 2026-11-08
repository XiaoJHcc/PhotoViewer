#!/usr/bin/env bash
# 清理可再生的验收中间态与过期归档；默认只预览，加 --yes 才删除。
#
#   release/testflight/   standard/、ai/ 各自只保留最近一个 .xcarchive；旧布局最新归档在
#                         新变体归档出现前保留一份（其余旧布局与导出目录删除）
#   artifacts/scratch/    清空
#   artifacts/acceptance/ 删除 photos-smoke.db*、settings-smoke.json、corrupt-*、
#                         rating-service.xmp、fixtures/、*-fixtures/
#   轮次目录              mac/ios/android-acceptance-<日期> 每个平台只保留最近 2 份
#
# 用法：
#   bash Tools/clean-artifacts.sh              # 预览
#   bash Tools/clean-artifacts.sh --yes        # 执行
#   bash Tools/clean-artifacts.sh --yes --keep-rounds 3

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RELEASE="$ROOT/release"
ARTIFACTS="$ROOT/artifacts"
DRY_RUN=1
KEEP_ROUNDS=2

while [[ $# -gt 0 ]]; do
    case "$1" in
        --yes) DRY_RUN=0 ;;
        --keep-rounds)
            KEEP_ROUNDS="${2:?--keep-rounds 需要数字}"
            shift
            ;;
        *)
            echo "未知参数：$1" >&2
            exit 2
            ;;
    esac
    shift
done

remove() {
    local target="$1"
    if [[ $DRY_RUN -eq 1 ]]; then
        echo "[dry] rm -rf -- $target"
    else
        echo "[clean] rm -rf -- $target"
        rm -rf -- "$target"
    fi
}

echo "== 清理可再生产物 =="

if [[ -d "$ARTIFACTS/scratch" ]]; then
    while IFS= read -r -d '' entry; do
        remove "$entry"
    done < <(find "$ARTIFACTS/scratch" -mindepth 1 -maxdepth 1 -print0)
fi

if [[ -d "$ARTIFACTS/acceptance" ]]; then
    while IFS= read -r -d '' entry; do
        remove "$entry"
    done < <(find "$ARTIFACTS/acceptance" -maxdepth 2 -type f \
        \( -name 'photos-smoke.db' -o -name 'photos-smoke.db-wal' -o -name 'photos-smoke.db-shm' \
        -o -name 'photos-legacy-layout.db' -o -name 'photos-legacy-layout.db-wal' -o -name 'photos-legacy-layout.db-shm' \
        -o -name 'settings-smoke.json' -o -name 'corrupt-*' -o -name 'rating-service.xmp' \) -print0)
    while IFS= read -r -d '' entry; do
        remove "$entry"
    done < <(find "$ARTIFACTS/acceptance" -maxdepth 2 -type d \( -name 'fixtures' -o -name '*-fixtures' \) -print0)
fi

echo "== 清理 release/testflight 旧归档 =="
if [[ -d "$RELEASE/testflight" ]]; then
    new_archive_count=0
    for edition in standard ai; do
        dir="$RELEASE/testflight/$edition"
        [[ -d "$dir" ]] || continue
        newest="$(ls -1dt "$dir"/*.xcarchive 2>/dev/null | head -n 1 || true)"
        if [[ -n "$newest" ]]; then
            new_archive_count=$((new_archive_count + 1))
        fi
        while IFS= read -r -d '' entry; do
            if [[ -n "$newest" && "$entry" == "$newest" ]]; then
                continue
            fi
            remove "$entry"
        done < <(find "$dir" -mindepth 1 -maxdepth 1 -print0)
        if [[ -n "$newest" ]]; then
            echo "[keep] $newest"
        fi
    done
    # 顶层只保留 standard/、ai/ 分版本目录。旧布局归档在新变体归档出现前保留最新一份，
    # 避免首次迁移前把仅有的符号材料（dSYMs）清空。
    legacy_newest=""
    if [[ "$new_archive_count" -eq 0 ]]; then
        legacy_newest="$(ls -1dt "$RELEASE/testflight"/*.xcarchive 2>/dev/null | head -n 1 || true)"
    fi
    while IFS= read -r -d '' entry; do
        case "$(basename "$entry")" in
            standard|ai) continue ;;
        esac
        if [[ -n "$legacy_newest" && "$entry" == "$legacy_newest" ]]; then
            continue
        fi
        remove "$entry"
    done < <(find "$RELEASE/testflight" -mindepth 1 -maxdepth 1 -print0)
    if [[ -n "$legacy_newest" ]]; then
        echo "[keep] ${legacy_newest}（旧布局最新归档；standard/ 或 ai/ 出现归档后收敛）"
    fi
fi

echo "== 轮次目录保留最近 $KEEP_ROUNDS 份 =="
for platform in mac ios android; do
    count=0
    while IFS= read -r entry; do
        [[ -z "$entry" ]] && continue
        count=$((count + 1))
        if [[ $count -gt $KEEP_ROUNDS ]]; then
            remove "$entry"
        fi
    done < <(ls -1dt "$ARTIFACTS/acceptance/$platform-acceptance-"* 2>/dev/null || true)
done

echo
if [[ $DRY_RUN -eq 1 ]]; then
    echo "以上为预览；加 --yes 执行。"
else
    echo "清理完成。当前体积："
    du -sh "$RELEASE" "$ARTIFACTS" 2>/dev/null || true
fi
