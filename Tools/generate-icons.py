#!/usr/bin/env python3
"""由 Design/icons.json 生成语义码位表、归一 Fluent 字体子集和对比报告。

普通运行复用已入库字体并生成共享码位表与报告；--refresh 在 macOS 上重新下载固定上游、
渲染 SF 参照并重建字体；--check 只校验生成物和字体覆盖，不写文件。刷新与检查均要求 fontTools。
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import html
import json
import math
import platform
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "Design/icons.json"
ASSETS = ROOT / "PhotoViewer/Assets/Icons"
MANIFEST = ASSETS / "icon-assets.json"
CSHARP = ROOT / "PhotoViewer.Shared/IconCatalog.g.cs"
REPORT = ROOT / "artifacts/acceptance/icon-catalog/index.html"
SWIFT = ROOT / "Tools/render-apple-symbols.swift"
FONT_FILES = {
    "regular": "FluentSystemIcons-Regular.ttf",
    "filled": "FluentSystemIcons-Filled.ttf",
}
REFERENCE_POINT_SIZE = 18


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def write_if_changed(path: Path, text: str) -> bool:
    if path.exists() and path.read_text(encoding="utf-8") == text:
        return False
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")
    return True


def download(url: str) -> bytes:
    request = urllib.request.Request(url, headers={"User-Agent": "PhotoViewer-Icon-Generator"})
    with urllib.request.urlopen(request, timeout=90) as response:
        return response.read()


def pascal(value: str) -> str:
    return "".join(part[:1].upper() + part[1:] for part in re.split(r"[.-]", value))


def csharp_string(value: str) -> str:
    return json.dumps(value, ensure_ascii=False)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def validate_source(source: dict) -> None:
    if source.get("schemaVersion") != 2:
        raise ValueError("icons.json schemaVersion 必须为 2")
    reference = source.get("reference", {})
    if reference.get("family") != "SF Symbols" or not reference.get("system"):
        raise ValueError("icons.json 必须固定 SF Symbols 参照系统版本")
    if reference.get("pointSize") != REFERENCE_POINT_SIZE or reference.get("weight") != "regular":
        raise ValueError("icons.json 的 SF 参照必须为 18 点 Regular")
    if reference.get("rule") != "mean-bounding-box-diagonal-ratio" or reference.get("alphaThreshold") != 0.5:
        raise ValueError("icons.json 的归一规则必须为 mean-bounding-box-diagonal-ratio，alpha 阈值为 0.5")
    seen = set()
    for icon in source.get("icons", []):
        icon_id = icon.get("id", "")
        if not re.fullmatch(r"[a-z][a-zA-Z]+\.[a-z][a-zA-Z0-9]+", icon_id):
            raise ValueError(f"语义 ID 不合法：{icon_id!r}")
        if icon_id in seen:
            raise ValueError(f"语义 ID 重复：{icon_id}")
        seen.add(icon_id)
        for platform_name in ("apple", "fluent"):
            if not isinstance(icon.get(platform_name), str) or not icon[platform_name]:
                raise ValueError(f"{icon_id} 缺少 {platform_name} 映射")
    if not seen:
        raise ValueError("icons.json 没有图标")


def glyph_bounds(font, glyph_name: str) -> tuple[float, float, float, float]:
    from fontTools.pens.boundsPen import BoundsPen

    glyph_set = font.getGlyphSet()
    pen = BoundsPen(glyph_set)
    glyph_set[glyph_name].draw(pen)
    if pen.bounds is None:
        raise ValueError(f"空字形：{glyph_name}")
    return pen.bounds


def ink_dimensions(bounds: tuple[float, float, float, float], units_per_em: int) -> dict:
    x0, y0, x1, y1 = bounds
    return {
        "width": (x1 - x0) * REFERENCE_POINT_SIZE / units_per_em,
        "height": (y1 - y0) * REFERENCE_POINT_SIZE / units_per_em,
    }


def measure_entries(entries: list[dict], font_paths: dict[str, Path], apple_metrics: dict) -> float:
    from fontTools.ttLib import TTFont

    fonts = {style: TTFont(path, recalcTimestamp=False) for style, path in font_paths.items()}
    ratios = []
    references = {}
    for entry in entries:
        style = "filled" if entry["filled"] else "regular"
        font = fonts[style]
        glyph_name = font.getBestCmap()[entry["fluentCodepoint"]]
        source_ink = ink_dimensions(glyph_bounds(font, glyph_name), font["head"].unitsPerEm)
        reference = apple_metrics[(entry["id"], REFERENCE_POINT_SIZE)]
        source_diagonal = math.hypot(source_ink["width"], source_ink["height"])
        reference_diagonal = math.hypot(reference["width"], reference["height"])
        if source_diagonal <= 0 or reference_diagonal <= 0:
            raise ValueError(f"无法测量图标墨迹：{entry['id']}")
        codepoint = entry["fluentCodepoint"]
        reference_size = (reference["width"], reference["height"])
        if codepoint in references and references[codepoint] != reference_size:
            raise ValueError(f"同一 Fluent 码位对应不同 SF 光学参照：U+{codepoint:X}")
        references[codepoint] = reference_size
        entry["fluentSourceInk"] = source_ink
        entry["sfInk"] = {
            "width": reference["width"],
            "height": reference["height"],
        }
        ratios.append(reference_diagonal / source_diagonal)
    return sum(ratios) / len(ratios)


def normalize_font(input_path: Path, output_path: Path, entries: list[dict], scale: float) -> None:
    from fontTools.misc.transform import Transform
    from fontTools.pens.transformPen import TransformPen
    from fontTools.pens.ttGlyphPen import TTGlyphPen
    from fontTools.ttLib import TTFont

    font = TTFont(input_path, recalcBBoxes=True, recalcTimestamp=False)
    cmap = font.getBestCmap()
    transformed = set()
    for entry in entries:
        codepoint = entry["fluentCodepoint"]
        entry["fluentScale"] = scale
        entry["fluentInk"] = {
            "width": entry["fluentSourceInk"]["width"] * scale,
            "height": entry["fluentSourceInk"]["height"] * scale,
        }
        if codepoint in transformed:
            continue
        transformed.add(codepoint)
        glyph_name = cmap[codepoint]
        transform = Transform(scale, 0, 0, scale, 0, 0)
        glyph_set = font.getGlyphSet()
        pen = TTGlyphPen(glyph_set)
        glyph_set[glyph_name].draw(TransformPen(pen, transform))
        font["glyf"][glyph_name] = pen.glyph()

    output_path.parent.mkdir(parents=True, exist_ok=True)
    font.save(output_path)


def refresh(source: dict, apple_dir: Path) -> dict:
    from fontTools import subset
    from fontTools.ttLib import TTFont

    if platform.system() != "Darwin":
        raise RuntimeError("归一字体刷新需要 macOS 上的实际 SF Symbols 渲染")
    current_system = f"macOS {platform.mac_ver()[0]}"
    if current_system != source["reference"]["system"]:
        raise RuntimeError(f"SF 参照系统不符：需要 {source['reference']['system']}，当前 {current_system}")
    _, apple_metrics = render_apple(source, apple_dir)
    ASSETS.mkdir(parents=True, exist_ok=True)
    fluent = source["families"]["fluent"]
    metadata = {
        style: json.loads(download(fluent[f"{style}Metadata"]))
        for style in ("regular", "filled")
    }
    fonts = {
        style: download(fluent[f"{style}Font"])
        for style in ("regular", "filled")
    }
    entries = []
    codes = {"regular": set(), "filled": set()}
    for icon in source["icons"]:
        fluent_name = icon["fluent"]
        style = "filled" if fluent_name.endswith("_filled") else "regular"
        if fluent_name not in metadata[style]:
            raise ValueError(f"Fluent 图标不存在：{icon['id']} = {fluent_name}")
        codepoint = metadata[style][fluent_name]
        codes[style].add(codepoint)
        entries.append({
            "id": icon["id"],
            "fluentCodepoint": codepoint,
            "filled": style == "filled",
        })

    with tempfile.TemporaryDirectory(prefix="pv-icon-fonts-") as temporary:
        temp = Path(temporary)
        source_paths = {}
        for style in ("regular", "filled"):
            source_paths[style] = temp / f"fluent-{style}.ttf"
            source_paths[style].write_bytes(fonts[style])
        scale = measure_entries(entries, source_paths, apple_metrics)
        for style in ("regular", "filled"):
            source_path = source_paths[style]
            subset_path = temp / f"fluent-{style}-subset.ttf"
            font = TTFont(source_path, recalcTimestamp=False)
            options = subset.Options()
            options.layout_features = ["*"]
            options.hinting = False
            options.notdef_outline = True
            subsetter = subset.Subsetter(options=options)
            subsetter.populate(unicodes=sorted(codes[style]))
            subsetter.subset(font)
            font.save(subset_path)
            style_entries = [entry for entry in entries if entry["filled"] == (style == "filled")]
            normalize_font(subset_path, ASSETS / FONT_FILES[style], style_entries, scale)

    (ASSETS / "FluentSystemIcons-LICENSE.txt").write_bytes(download(fluent["license"]))
    manifest = {
        "schemaVersion": 2,
        "families": source["families"],
        "reference": source["reference"],
        "icons": entries,
        "files": {},
    }
    for filename in FONT_FILES.values():
        manifest["files"][filename] = sha256(ASSETS / filename)
    MANIFEST.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return manifest


def render_csharp(source: dict, manifest: dict) -> str:
    by_id = {entry["id"]: entry for entry in manifest["icons"]}
    lines = [
        "// <auto-generated>",
        "// 由 Tools/generate-icons.py 从 Design/icons.json 与 icon-assets.json 生成，请勿手改。",
        "// </auto-generated>",
        "",
        "namespace PhotoViewer.Shared;",
        "",
        "/// <summary>跨壳语义图标清单；字体对象与原生图标对象由各平台头部创建。</summary>",
        "public static class IconCatalog",
        "{",
    ]
    for icon in source["icons"]:
        lines.append(f"    public const string {pascal(icon['id'])} = \"{icon['id']}\";")
    lines.extend(["", "    /// <summary>Apple SF Symbol 名称；未知语义显式失败。</summary>"])
    lines.append("    public static string Apple(string id) => id switch")
    lines.append("    {")
    for icon in source["icons"]:
        lines.append(f"        {pascal(icon['id'])} => {csharp_string(icon['apple'])},")
    lines.extend([
        "        _ => throw new ArgumentException($\"未定义 Apple 图标语义：{id}\", nameof(id)),",
        "    };",
        "",
        "    /// <summary>共享 Fluent 字形码位；未知语义显式失败。</summary>",
        "    public static int FluentCodePoint(string id) => id switch",
        "    {",
    ])
    for icon in source["icons"]:
        lines.append(f"        {pascal(icon['id'])} => 0x{by_id[icon['id']]['fluentCodepoint']:X},")
    lines.extend([
        "        _ => throw new ArgumentException($\"未定义 Fluent 图标语义：{id}\", nameof(id)),",
        "    };",
        "",
        "    public static string FluentGlyph(string id) => char.ConvertFromUtf32(FluentCodePoint(id));",
        "",
        "    public static bool FluentFilled(string id) => id switch",
        "    {",
    ])
    for icon in source["icons"]:
        value = "true" if by_id[icon["id"]]["filled"] else "false"
        lines.append(f"        {pascal(icon['id'])} => {value},")
    lines.extend([
        "        _ => throw new ArgumentException($\"未定义 Fluent 图标语义：{id}\", nameof(id)),",
        "    };",
        "}",
        "",
    ])
    return "\n".join(lines)


def check(source: dict, manifest: dict) -> list[str]:
    from fontTools.ttLib import TTFont

    errors = []
    source_icons = {icon["id"]: icon for icon in source["icons"]}
    manifest_icons = {entry["id"]: entry for entry in manifest.get("icons", [])}
    if set(source_icons) != set(manifest_icons):
        errors.append("生成资产与 icons.json 的语义集合不一致")
    if manifest.get("families") != source["families"]:
        errors.append("生成资产与 icons.json 的上游版本不一致")
    if manifest.get("reference") != source["reference"]:
        errors.append("生成资产与 icons.json 的 SF 参照不一致")
    if not CSHARP.exists() or render_csharp(source, manifest) != CSHARP.read_text(encoding="utf-8"):
        errors.append("PhotoViewer.Shared/IconCatalog.g.cs 不是最新产物")
    ratios = [
        math.hypot(entry["sfInk"]["width"], entry["sfInk"]["height"])
        / math.hypot(entry["fluentSourceInk"]["width"], entry["fluentSourceInk"]["height"])
        for entry in manifest_icons.values()
    ]
    expected_scale = sum(ratios) / len(ratios) if ratios else 0
    for style, filename in FONT_FILES.items():
        path = ASSETS / filename
        if not path.exists():
            errors.append(f"缺少字体：{filename}")
            continue
        if manifest.get("files", {}).get(filename) != sha256(path):
            errors.append(f"字体摘要不符：{filename}")
        font = TTFont(path)
        cmap = font.getBestCmap()
        for icon_id, entry in manifest_icons.items():
            expected_style = "filled" if entry["filled"] else "regular"
            if style != expected_style:
                continue
            codepoint = entry["fluentCodepoint"]
            if codepoint not in cmap:
                errors.append(f"{filename} 缺少 {icon_id} U+{codepoint:04X}")
                continue
            glyph_name = cmap[codepoint]
            bounds = glyph_bounds(font, glyph_name)
            if abs(entry["fluentScale"] - expected_scale) > 1e-9:
                errors.append(f"{icon_id} 使用了单项缩放而非全目录平均倍率")
            actual = ink_dimensions(bounds, font["head"].unitsPerEm)
            expected = {
                "width": entry["fluentSourceInk"]["width"] * expected_scale,
                "height": entry["fluentSourceInk"]["height"] * expected_scale,
            }
            if abs(actual["width"] - expected["width"]) > 0.06 or abs(actual["height"] - expected["height"]) > 0.06:
                errors.append(f"{filename} 的 {icon_id} 未按全目录平均倍率归一")
            if abs(actual["width"] - entry["fluentInk"]["width"]) > 0.06 or abs(actual["height"] - entry["fluentInk"]["height"]) > 0.06:
                errors.append(f"{filename} 的 {icon_id} 归一指标不一致")
    if not (ASSETS / "FluentSystemIcons-LICENSE.txt").exists():
        errors.append("缺少 Fluent 许可证")
    return errors


def render_apple(source: dict, output: Path) -> tuple[dict[tuple[str, int], str], dict[tuple[str, int], dict]]:
    if platform.system() != "Darwin":
        raise RuntimeError("Apple 实际符号对比只能在 macOS 生成；其他平台可使用 --no-apple")
    swift = shutil.which("swift")
    if not swift:
        raise RuntimeError("找不到 swift，无法生成 Apple 实际符号对比")
    symbols = [{"id": icon["id"], "name": icon["apple"]} for icon in source["icons"]]
    with tempfile.TemporaryDirectory(prefix="pv-apple-symbols-") as temporary:
        temp = Path(temporary)
        (temp / "symbols.json").write_text(json.dumps(symbols), encoding="utf-8")
        subprocess.run([swift, str(SWIFT), str(temp / "symbols.json"), str(output)], check=True)
        images = {}
        for icon in source["icons"]:
            stem = icon["id"].replace(".", "_")
            for size in (18, 24):
                path = output / f"{stem}-{size}.png"
                images[(icon["id"], size)] = base64.b64encode(path.read_bytes()).decode("ascii")
        metrics = {
            (entry["id"], int(entry["pointSize"])): entry
            for entry in read_json(output / "metrics.json")
        }
        return images, metrics


def render_report(source: dict, manifest: dict, output: Path, include_apple: bool) -> None:
    by_id = {entry["id"]: entry for entry in manifest["icons"]}
    apple_dir = output.parent / "apple"
    apple_images, _ = render_apple(source, apple_dir) if include_apple else ({}, {})
    font_css = []
    for family, style in (("pv-fluent-regular", "regular"), ("pv-fluent-filled", "filled")):
        data = base64.b64encode((ASSETS / FONT_FILES[style]).read_bytes()).decode("ascii")
        font_css.append(f"@font-face{{font-family:{family};src:url(data:font/ttf;base64,{data}) format('truetype');font-display:block}}")
    rows = []
    current_group = None
    for icon in source["icons"]:
        if icon["group"] != current_group:
            current_group = icon["group"]
            rows.append(f"<tr class='group'><th colspan='2'>{html.escape(current_group)}</th></tr>")
        entry = by_id[icon["id"]]
        cells = []
        for size in (18, 24):
            image = apple_images.get((icon["id"], size))
            apple = f"<img src='data:image/png;base64,{image}' width='32' height='32'>" if image else "<span class='missing'>未生成</span>"
            fluent_family = "filled" if entry["filled"] else "regular"
            fluent = f"<span class='glyph fluent-{fluent_family}' style='font-size:{size}px'>&#x{entry['fluentCodepoint']:X};</span>"
            cells.append((apple, fluent))
        samples = "<div class='sizes'>" + "".join(
            f"<div class='sample'><div class='pair'><div>{cells[index][0]}</div><div>{cells[index][1]}</div></div><small>{size}</small></div>"
            for index, size in enumerate((18, 24))
        ) + "</div>"
        sf = entry["sfInk"]
        raw = entry["fluentSourceInk"]
        normalized = entry["fluentInk"]
        names = (
            f"<div class='names'><span>Apple · {html.escape(icon['apple'])}</span>"
            f"<span>Fluent · {html.escape(icon['fluent'])}</span>"
            f"<span>18 点墨迹 · SF {sf['width']:.2f}×{sf['height']:.2f} · Fluent {raw['width']:.2f}×{raw['height']:.2f}"
            f" → {normalized['width']:.2f}×{normalized['height']:.2f} · ×{entry['fluentScale']:.4f}</span></div>"
        )
        rows.append(
            f"<tr data-search='{html.escape(icon['id'] + ' ' + icon['title'])}'><td class='semantic'><b>{html.escape(icon['title'])}</b><code>{icon['id']}</code>{names}</td>"
            f"<td colspan='2'>{samples}</td></tr>"
        )
    document = """<!doctype html><html lang='zh-CN'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>PhotoViewer 语义图标对比</title><style>
__FONTS__
:root{color-scheme:dark}*{box-sizing:border-box}body{margin:24px;background:#111214;color:#f2f2f2;font:14px -apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif}header{display:flex;align-items:center;gap:20px;margin-bottom:20px}h1{font-size:20px;margin:0}input{width:min(420px,60vw);padding:9px 12px;border:1px solid #45464b;border-radius:8px;background:#1c1d20;color:inherit}table{width:100%;border-collapse:collapse}th,td{text-align:left;border-bottom:1px solid #2b2c30;padding:12px 10px;vertical-align:middle}.group th{padding-top:26px;color:#a9abb2;font-size:13px}.semantic{width:52%;min-width:480px}.semantic b{display:block;margin-bottom:4px}.semantic code{display:block;color:#9ea1a9}.names{display:flex;flex-direction:column;color:#777b85;font:11px ui-monospace,monospace;margin-top:8px;gap:2px}.sizes{display:flex;gap:30px}.sample{text-align:center}.sample small{display:block;color:#777b85;margin-top:4px}.pair{display:grid;grid-template-columns:repeat(2,56px);align-items:center;justify-items:center;min-height:52px}.pair>div{width:56px;height:52px;display:grid;place-items:center;border-left:1px solid #26272b}.pair>div:last-child{border-right:1px solid #26272b}.glyph{font-family:pv-fluent-regular;line-height:1}.fluent-filled{font-family:pv-fluent-filled}img{width:32px;height:32px;filter:brightness(0) invert(1)}.missing{color:#ff7b72;font-size:11px}@media(max-width:800px){.semantic{min-width:280px}.names{display:none}.sizes{gap:8px}.pair{grid-template-columns:repeat(2,40px)}.pair>div{width:40px}}
</style></head><body><header><h1>PhotoViewer 语义图标对比</h1><input id='filter' placeholder='搜索语义 ID 或中文名称'></header>
<div>每行从左到右：Apple 实际系统渲染 / 共享 Fluent 归一字形；每档为 18 / 24 点，Apple 保持 32 点原始画布。</div>
<table>__ROWS__</table><script>document.getElementById('filter').addEventListener('input',e=>{const q=e.target.value.toLowerCase();document.querySelectorAll('tr[data-search]').forEach(r=>r.hidden=!r.dataset.search.toLowerCase().includes(q))})</script></body></html>"""
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(document.replace("__FONTS__", "\n".join(font_css)).replace("__ROWS__", "\n".join(rows)), encoding="utf-8")
    metrics = {
        "reference": manifest["reference"],
        "icons": [
            {
                "id": entry["id"],
                "fluentScale": entry["fluentScale"],
                "sfInk": entry["sfInk"],
                "fluentSourceInk": entry["fluentSourceInk"],
                "fluentInk": entry["fluentInk"],
            }
            for entry in manifest["icons"]
        ],
    }
    (output.parent / "metrics.json").write_text(json.dumps(metrics, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--refresh", action="store_true", help="重新下载固定上游、渲染 SF 并重建归一字体")
    parser.add_argument("--check", action="store_true", help="只检查生成物，不写文件")
    parser.add_argument("--no-apple", action="store_true", help="非 macOS 环境只生成 Fluent 对比")
    parser.add_argument("--report", type=Path, default=REPORT)
    args = parser.parse_args()
    source = read_json(SOURCE)
    validate_source(source)
    if args.check:
        if not MANIFEST.exists():
            print("缺少生成资产 icon-assets.json", file=sys.stderr)
            return 1
        errors = check(source, read_json(MANIFEST))
        for error in errors:
            print(f"FAIL {error}", file=sys.stderr)
        if not errors:
            print(f"PASS {len(source['icons'])} 个语义图标；码位、字体摘要、归一指标与生成码位表一致")
        return 1 if errors else 0
    if args.refresh and args.no_apple:
        parser.error("--refresh 需要 Apple 实际渲染，不能与 --no-apple 合用")
    manifest = refresh(source, args.report.parent / "apple") if args.refresh or not MANIFEST.exists() else read_json(MANIFEST)
    write_if_changed(CSHARP, render_csharp(source, manifest))
    errors = check(source, manifest)
    if errors:
        for error in errors:
            print(f"FAIL {error}", file=sys.stderr)
        return 1
    render_report(source, manifest, args.report, not args.no_apple)
    print(f"PASS {len(source['icons'])} 个语义图标；报告：{args.report}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
