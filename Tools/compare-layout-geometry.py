#!/usr/bin/env python3
"""比对两份主窗口几何：样机 JSON 与 macOS 验收文本（自动识别格式）。

用法：
  python3 Tools/compare-layout-geometry.py <a> <b> [--tolerance 0.5]

输入：
  - 样机 JSON：{"source":"prototype","layouts":{"Col-1200x850":{"root.filePane":[x,y,w,h],...},...}}
  - macOS 验收文本：每行 `布局<TAB>节点名<TAB>(x,y,w,h)`
    （PhotoViewer.Mac --pv-native-layout 导出的 native-layout-geometry.txt）

只比对双方共有的布局与节点；单方独有的布局/节点单独列出，不算失败。差异表列出布局、节点、两边数值与最大轴差；
存在共有节点差异时退出码 1，否则 0。
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

TEXT_LINE = re.compile(r"^(?P<layout>[^\t]+)\t(?P<node>[^\t]+)\t\((?P<x>-?[\d.]+),(?P<y>-?[\d.]+),(?P<w>-?[\d.]+),(?P<h>-?[\d.]+)\)\s*$")


def load_text(text: str) -> dict[str, dict[str, list[float]]]:
    layouts: dict[str, dict[str, list[float]]] = {}
    for line in text.splitlines():
        if not line.strip():
            continue
        match = TEXT_LINE.match(line)
        if not match:
            raise ValueError(f"无法解析的几何行：{line}")
        frame = [float(match.group(key)) for key in ("x", "y", "w", "h")]
        layouts.setdefault(match.group("layout"), {})[match.group("node")] = frame
    return layouts


def load_json(text: str) -> dict[str, dict[str, list[float]]]:
    payload = json.loads(text)
    if isinstance(payload, dict) and isinstance(payload.get("layouts"), dict):
        payload = payload["layouts"]
    if not isinstance(payload, dict):
        raise ValueError("JSON 缺少 layouts 对象")
    layouts: dict[str, dict[str, list[float]]] = {}
    for layout_name, nodes in payload.items():
        if not isinstance(nodes, dict):
            raise ValueError(f"布局 {layout_name} 不是节点字典")
        converted = {}
        for node, frame in nodes.items():
            if not isinstance(frame, (list, tuple)) or len(frame) != 4:
                raise ValueError(f"节点 {layout_name}/{node} 不是 [x,y,w,h]")
            converted[node] = [float(value) for value in frame]
        layouts[layout_name] = converted
    return layouts


def load(path: Path) -> dict[str, dict[str, list[float]]]:
    text = path.read_text(encoding="utf-8")
    if text.lstrip().startswith("{"):
        return load_json(text)
    return load_text(text)


def main() -> int:
    parser = argparse.ArgumentParser(description="比对主窗口几何")
    parser.add_argument("a", type=Path, help="第一份几何（样机 JSON 或 Mac 文本）")
    parser.add_argument("b", type=Path, help="第二份几何（样机 JSON 或 Mac 文本）")
    parser.add_argument("--tolerance", type=float, default=0.5, help="单轴允许误差（点），默认 0.5")
    args = parser.parse_args()

    layouts_a = load(args.a)
    layouts_b = load(args.b)
    common_layouts = sorted(set(layouts_a) & set(layouts_b))
    only_a_layouts = sorted(set(layouts_a) - set(layouts_b))
    only_b_layouts = sorted(set(layouts_b) - set(layouts_a))

    differences: list[tuple[str, str, list[float], list[float], float]] = []
    unique_nodes: dict[str, tuple[list[str], list[str]]] = {}
    common_nodes = 0
    for layout_name in common_layouts:
        nodes_a = layouts_a[layout_name]
        nodes_b = layouts_b[layout_name]
        for node in sorted(set(nodes_a) & set(nodes_b)):
            common_nodes += 1
            frame_a = nodes_a[node]
            frame_b = nodes_b[node]
            delta = max(abs(x - y) for x, y in zip(frame_a, frame_b))
            if delta > args.tolerance:
                differences.append((layout_name, node, frame_a, frame_b, delta))
        only_a = sorted(set(nodes_a) - set(nodes_b))
        only_b = sorted(set(nodes_b) - set(nodes_a))
        if only_a or only_b:
            unique_nodes[layout_name] = (only_a, only_b)

    print(f"共有布局 {len(common_layouts)} 个，共有节点 {common_nodes} 个，差异 {len(differences)} 个（容差 {args.tolerance} 点）")
    if only_a_layouts:
        print("仅第一份有的布局：" + "、".join(only_a_layouts))
    if only_b_layouts:
        print("仅第二份有的布局：" + "、".join(only_b_layouts))

    if differences:
        print()
        print(f"{'布局':<16} {'节点':<28} {'第一份 (x,y,w,h)':<28} {'第二份 (x,y,w,h)':<28} 最大差")
        for layout_name, node, frame_a, frame_b, delta in differences:
            print(f"{layout_name:<16} {node:<28} {fmt(frame_a):<28} {fmt(frame_b):<28} {delta:.2f}")

    if unique_nodes:
        print()
        print("单方独有的节点（不算失败）：")
        for layout_name in common_layouts:
            if layout_name not in unique_nodes:
                continue
            only_a, only_b = unique_nodes[layout_name]
            if only_a:
                print(f"  {layout_name} 仅第一份：{summarize(only_a)}")
            if only_b:
                print(f"  {layout_name} 仅第二份：{summarize(only_b)}")

    return 1 if differences else 0


def fmt(frame: list[float]) -> str:
    return "(" + ",".join(f"{value:g}" for value in frame) + ")"


def summarize(names: list[str]) -> str:
    """把 card0..cardN 这类同构节点压成一行，避免把整条列表刷满屏幕。"""
    card_pattern = re.compile(r"^card(\d+)\.(.+)$")
    cards: dict[str, list[int]] = {}
    others: list[str] = []
    for name in names:
        match = card_pattern.match(name)
        if match:
            cards.setdefault(match.group(2), []).append(int(match.group(1)))
        else:
            others.append(name)
    parts: list[str] = []
    if cards:
        indices = sorted({index for values in cards.values() for index in values})
        span = f"card{indices[0]}" if len(indices) == 1 else f"card{indices[0]}..card{indices[-1]}"
        span += f"（{len(indices)} 张）的 " + "、".join(sorted(cards))
        parts.append(span)
    parts.extend(others)
    return "；".join(parts)


if __name__ == "__main__":
    sys.exit(main())
