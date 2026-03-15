#!/bin/bash
# ==========================================================
# PhotoViewer 一键安装助手
# 双击此文件即可移除 macOS 的下载隔离限制，无需开发者证书
# ==========================================================

APP="/Applications/PhotoViewer.Mac.app"

echo "========================================"
echo "  PhotoViewer 安装助手"
echo "========================================"
echo ""

if [ -d "$APP" ]; then
    echo "✅ 找到 PhotoViewer.Mac.app，正在移除隔离属性..."
    xattr -dr com.apple.quarantine "$APP"
    echo ""
    echo "✅ 完成！现在可以正常双击打开 PhotoViewer 了。"
else
    echo "⚠️  未在 /Applications 中找到 PhotoViewer.Mac.app"
    echo ""
    echo "请先将 DMG 中的 PhotoViewer.Mac.app 拖入"
    echo "右侧 Applications 文件夹，然后重新双击本脚本。"
fi

echo ""
echo "按回车键退出..."
read

