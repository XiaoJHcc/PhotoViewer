#!/usr/bin/env python3
"""生成 Web 参考壳的图标字体子集。

码位来源：Design/tokens.json 中所有取值形如 "U+XXXX" 的 icon.* 令牌（TOOLBAR-04、FILM-05、
FILTER-08、MENU-02）；名称以 Filled 结尾的码位（icon.starFilled）取自 icon.fontSolid（uicons solid-straight）。

产物：
  src/assets/uicons-pv.woff2        uicons-regular-rounded 子集（描边字形）
  src/assets/uicons-pv-solid.woff2  uicons-solid-straight 子集（实心星、实心叉）

依赖 fonttools（含 brotli）。默认调用 PATH 上的 pyftsubset，可用 PYFTSUBSET 指定：

  python3 PhotoViewer.Web/tools/subset-icons.py
  PYFTSUBSET=/tmp/fontvenv/bin/pyftsubset python3 PhotoViewer.Web/tools/subset-icons.py

solid-straight 字体在 PhotoViewer/Assets/Fonts；也可用 --solid-source 指定，最后回退 git 历史（78ed013^）。
"""

import argparse
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WEB = ROOT / "PhotoViewer.Web"
REGULAR_TTF = ROOT / "PhotoViewer/Assets/Fonts/uicons-regular-rounded.ttf"
SOLID_TTF = ROOT / "PhotoViewer/Assets/Fonts/uicons-solid-straight.ttf"
SOLID_GIT = "78ed013^:PhotoViewer/Assets/Fonts/uicons-solid-straight.ttf"
OUT_REGULAR = WEB / "src/assets/uicons-pv.woff2"
OUT_SOLID = WEB / "src/assets/uicons-pv-solid.woff2"

# 实心字形：取 tokens.json 中 icon.*Filled 码位（icon.fontSolid）。
SOLID_SUFFIX = "Filled"


def token_codepoints():
    tokens = json.loads((ROOT / "Design/tokens.json").read_text(encoding="utf-8"))
    found = {}
    for name, entry in tokens.get("tokens", tokens).items():
        if not name.startswith("icon.") or name in ("icon.font", "icon.fontSolid"):
            continue
        value = entry.get("value") if isinstance(entry, dict) else entry
        match = re.fullmatch(r"U\+([0-9A-Fa-f]{4,6})", str(value))
        if match:
            found[name] = match.group(1).upper()
    if not found:
        raise SystemExit("Design/tokens.json 中没有 icon.* 码位令牌")
    return found


def find_solid(explicit):
    if explicit:
        path = Path(explicit)
        if not path.exists():
            raise SystemExit(f"找不到 solid 字体：{path}")
        return path
    if SOLID_TTF.exists():
        return SOLID_TTF
    temp = Path(tempfile.gettempdir()) / "uicons-solid-straight.ttf"
    try:
        data = subprocess.run(
            ["git", "-C", str(ROOT), "show", SOLID_GIT],
            check=True, capture_output=True,
        ).stdout
        temp.write_bytes(data)
        return temp
    except subprocess.CalledProcessError:
        raise SystemExit(
            "找不到 uicons-solid-straight.ttf：请用 --solid-source 指定，或确认 git 历史可读"
        )


def run_subset(pyftsubset, source, codepoints, out):
    out.parent.mkdir(parents=True, exist_ok=True)
    args = [
        pyftsubset, str(source),
        f"--unicodes={','.join(codepoints)}",
        "--flavor=woff2",
        "--layout-features=*",
        "--no-hinting",
        "--desubroutinize",
        f"--output-file={out}",
    ]
    subprocess.run(args, check=True)
    return out.stat().st_size


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--pyftsubset", default="pyftsubset", help="pyftsubset 可执行文件")
    parser.add_argument("--solid-source", default=None, help="uicons-solid-straight.ttf 路径")
    args = parser.parse_args()

    icons = token_codepoints()
    regular = sorted({code for name, code in icons.items() if not name.endswith(SOLID_SUFFIX)})
    solid = sorted({code for name, code in icons.items() if name.endswith(SOLID_SUFFIX)})

    size_regular = run_subset(args.pyftsubset, REGULAR_TTF, regular, OUT_REGULAR)
    solid_source = find_solid(args.solid_source)
    size_solid = run_subset(args.pyftsubset, solid_source, solid, OUT_SOLID)

    for name, code in sorted(icons.items()):
        print(f"{name:32s} U+{code}")
    print(f"regular  {len(regular):2d} 码位  {size_regular:6d} B  {OUT_REGULAR.relative_to(ROOT)}")
    print(f"solid    {len(solid):2d} 码位  {size_solid:6d} B  {OUT_SOLID.relative_to(ROOT)}")


if __name__ == "__main__":
    sys.exit(main())
