#!/usr/bin/env python3
"""由 Design/icons.json 生成三端语义码位表、随包字体子集和对比报告。

普通运行复用已入库字体并生成共享码位表与报告；--refresh 重新下载固定上游并重建字体；
--check 只校验生成物和字体覆盖，不写文件。刷新与检查均要求 fontTools。
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import html
import json
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
    "windowsRegular": "FluentSystemIcons-Regular.ttf",
    "windowsFilled": "FluentSystemIcons-Filled.ttf",
    "androidRegular": "MaterialSymbolsRounded-Regular.ttf",
    "androidFilled": "MaterialSymbolsRounded-Filled.ttf",
}


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
    if source.get("schemaVersion") != 1:
        raise ValueError("icons.json schemaVersion 必须为 1")
    seen = set()
    for icon in source.get("icons", []):
        icon_id = icon.get("id", "")
        if not re.fullmatch(r"[a-z][a-zA-Z]+\.[a-z][a-zA-Z0-9]+", icon_id):
            raise ValueError(f"语义 ID 不合法：{icon_id!r}")
        if icon_id in seen:
            raise ValueError(f"语义 ID 重复：{icon_id}")
        seen.add(icon_id)
        for platform_name in ("apple", "windows", "android"):
            if not isinstance(icon.get(platform_name), str) or not icon[platform_name]:
                raise ValueError(f"{icon_id} 缺少 {platform_name} 映射")
        if icon.get("androidFill", 0) not in (0, 1):
            raise ValueError(f"{icon_id} androidFill 只能为 0 或 1")
    if not seen:
        raise ValueError("icons.json 没有图标")


def refresh(source: dict) -> dict:
    from fontTools import subset
    from fontTools.ttLib import TTFont
    from fontTools.varLib.instancer import instantiateVariableFont

    ASSETS.mkdir(parents=True, exist_ok=True)
    families = source["families"]
    windows = families["windows"]
    android = families["android"]
    windows_metadata = {
        style: json.loads(download(windows[f"{style}Metadata"]))
        for style in ("regular", "filled")
    }
    material_codepoints = {}
    for line in download(android["codepoints"]).decode("utf-8").splitlines():
        name, codepoint = line.split()
        material_codepoints[name] = int(codepoint, 16)

    windows_fonts = {
        style: download(windows[f"{style}Font"]) for style in ("regular", "filled")
    }
    material_font = download(android["font"])
    manifest_icons = []
    with tempfile.TemporaryDirectory(prefix="pv-icon-fonts-") as temporary:
        temp = Path(temporary)
        for style, data in windows_fonts.items():
            (temp / f"fluent-{style}.ttf").write_bytes(data)
        (temp / "material.ttf").write_bytes(material_font)

        windows_codes = {"regular": set(), "filled": set()}
        android_codes = {0: set(), 1: set()}
        for icon in source["icons"]:
            windows_name = icon["windows"]
            windows_style = "filled" if windows_name.endswith("_filled") else "regular"
            if windows_name not in windows_metadata[windows_style]:
                raise ValueError(f"Fluent 图标不存在：{icon['id']} = {windows_name}")
            windows_code = windows_metadata[windows_style][windows_name]
            windows_codes[windows_style].add(windows_code)
            android_name = icon["android"]
            if android_name not in material_codepoints:
                raise ValueError(f"Material 图标不存在：{icon['id']} = {android_name}")
            android_code = material_codepoints[android_name]
            android_fill = icon.get("androidFill", 0)
            android_codes[android_fill].add(android_code)
            manifest_icons.append({
                "id": icon["id"],
                "windowsCodepoint": windows_code,
                "windowsFilled": windows_style == "filled",
                "androidCodepoint": android_code,
                "androidFill": android_fill,
            })

        def subset_font(input_path: Path, output_name: str, codepoints: set[int], axes=None):
            font = TTFont(input_path)
            if axes is not None:
                font = instantiateVariableFont(font, axes, updateFontNames=False)
            options = subset.Options()
            options.layout_features = ["*"]
            options.hinting = True
            options.notdef_outline = True
            subsetter = subset.Subsetter(options=options)
            subsetter.populate(unicodes=sorted(codepoints))
            subsetter.subset(font)
            output = ASSETS / output_name
            font.save(output)

        subset_font(temp / "fluent-regular.ttf", FONT_FILES["windowsRegular"], windows_codes["regular"])
        subset_font(temp / "fluent-filled.ttf", FONT_FILES["windowsFilled"], windows_codes["filled"])
        base_axes = dict(android["axes"])
        for fill in (0, 1):
            axes = dict(base_axes)
            axes["FILL"] = fill
            key = "androidFilled" if fill else "androidRegular"
            subset_font(temp / "material.ttf", FONT_FILES[key], android_codes[fill], axes)

    (ASSETS / "FluentSystemIcons-LICENSE.txt").write_bytes(download(windows["license"]))
    (ASSETS / "MaterialSymbols-LICENSE.txt").write_bytes(download(android["license"]))
    manifest = {
        "schemaVersion": 1,
        "families": families,
        "icons": manifest_icons,
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
        "    /// <summary>Windows 字形码位；未知语义显式失败。</summary>",
        "    public static int WindowsCodePoint(string id) => id switch",
        "    {",
    ])
    for icon in source["icons"]:
        lines.append(f"        {pascal(icon['id'])} => 0x{by_id[icon['id']]['windowsCodepoint']:X},")
    lines.extend([
        "        _ => throw new ArgumentException($\"未定义 Windows 图标语义：{id}\", nameof(id)),",
        "    };",
        "",
        "    public static string WindowsGlyph(string id) => char.ConvertFromUtf32(WindowsCodePoint(id));",
        "",
        "    public static bool WindowsFilled(string id) => id switch",
        "    {",
    ])
    for icon in source["icons"]:
        value = "true" if by_id[icon["id"]]["windowsFilled"] else "false"
        lines.append(f"        {pascal(icon['id'])} => {value},")
    lines.extend([
        "        _ => throw new ArgumentException($\"未定义 Windows 图标语义：{id}\", nameof(id)),",
        "    };",
        "",
        "    /// <summary>Android 字形码位；未知语义显式失败。</summary>",
        "    public static int AndroidCodePoint(string id) => id switch",
        "    {",
    ])
    for icon in source["icons"]:
        lines.append(f"        {pascal(icon['id'])} => 0x{by_id[icon['id']]['androidCodepoint']:X},")
    lines.extend([
        "        _ => throw new ArgumentException($\"未定义 Android 图标语义：{id}\", nameof(id)),",
        "    };",
        "",
        "    public static string AndroidGlyph(string id) => char.ConvertFromUtf32(AndroidCodePoint(id));",
        "",
        "    public static bool AndroidFilled(string id) => id switch",
        "    {",
    ])
    for icon in source["icons"]:
        value = "true" if by_id[icon["id"]]["androidFill"] else "false"
        lines.append(f"        {pascal(icon['id'])} => {value},")
    lines.extend([
        "        _ => throw new ArgumentException($\"未定义 Android 图标语义：{id}\", nameof(id)),",
        "    };",
        "}",
        "",
    ])
    return "\n".join(lines)


def check(source: dict, manifest: dict) -> list[str]:
    from fontTools.ttLib import TTFont

    errors = []
    source_icons = {icon["id"]: icon for icon in source["icons"]}
    manifest_icons = {icon["id"]: icon for icon in manifest.get("icons", [])}
    if set(source_icons) != set(manifest_icons):
        errors.append("生成资产与 icons.json 的语义集合不一致")
    if manifest.get("families") != source["families"]:
        errors.append("生成资产与 icons.json 的上游版本不一致")
    if not CSHARP.exists() or render_csharp(source, manifest) != CSHARP.read_text(encoding="utf-8"):
        errors.append("PhotoViewer.Shared/IconCatalog.g.cs 不是最新产物")
    for key, filename in FONT_FILES.items():
        path = ASSETS / filename
        if not path.exists():
            errors.append(f"缺少字体：{filename}")
            continue
        if manifest.get("files", {}).get(filename) != sha256(path):
            errors.append(f"字体摘要不符：{filename}")
        cmap = set()
        for table in TTFont(path)["cmap"].tables:
            cmap.update(table.cmap)
        for icon_id, icon in manifest_icons.items():
            platform_key = "windows" if key.startswith("windows") else "android"
            filled = icon[f"{platform_key}Filled"] if platform_key == "windows" else icon["androidFill"] == 1
            expected_style = key.endswith("Filled")
            if filled != expected_style:
                continue
            codepoint = icon[f"{platform_key}Codepoint"]
            if codepoint not in cmap:
                errors.append(f"{filename} 缺少 {icon_id} U+{codepoint:04X}")
    for filename in ("FluentSystemIcons-LICENSE.txt", "MaterialSymbols-LICENSE.txt"):
        if not (ASSETS / filename).exists():
            errors.append(f"缺少许可证：{filename}")
    return errors


def render_apple(source: dict, output: Path) -> dict[tuple[str, int], str]:
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
        return images


def render_report(source: dict, manifest: dict, output: Path, include_apple: bool) -> None:
    by_id = {entry["id"]: entry for entry in manifest["icons"]}
    apple_dir = output.parent / "apple"
    apple_images = render_apple(source, apple_dir) if include_apple else {}
    font_css = []
    for family, filename in (
        ("pv-fluent-regular", "windowsRegular"),
        ("pv-fluent-filled", "windowsFilled"),
        ("pv-material-regular", "androidRegular"),
        ("pv-material-filled", "androidFilled"),
    ):
        data = base64.b64encode((ASSETS / FONT_FILES[filename]).read_bytes()).decode("ascii")
        font_css.append(f"@font-face{{font-family:{family};src:url(data:font/ttf;base64,{data}) format('truetype');font-display:block}}")
    rows = []
    current_group = None
    for icon in source["icons"]:
        if icon["group"] != current_group:
            current_group = icon["group"]
            rows.append(f"<tr class='group'><th colspan='4'>{html.escape(current_group)}</th></tr>")
        entry = by_id[icon["id"]]
        cells = []
        for size in (18, 24):
            image = apple_images.get((icon["id"], size))
            apple = f"<img src='data:image/png;base64,{image}' width='{size * 2}' height='{size * 2}'>" if image else "<span class='missing'>未生成</span>"
            windows_family = "filled" if entry["windowsFilled"] else "regular"
            android_family = "filled" if entry["androidFill"] else "regular"
            windows = f"<span class='glyph fluent-{windows_family}' style='font-size:{size}px'>&#x{entry['windowsCodepoint']:X};</span>"
            android = f"<span class='glyph material-{android_family}' style='font-size:{size}px'>&#x{entry['androidCodepoint']:X};</span>"
            cells.append((apple, windows, android))
        samples = "<div class='sizes'>" + "".join(
            f"<div class='sample'><div class='triple'><div>{cells[index][0]}</div><div>{cells[index][1]}</div><div>{cells[index][2]}</div></div><small>{size}</small></div>"
            for index, size in enumerate((18, 24))
        ) + "</div>"
        names = (
            f"<div class='names'><span>Apple · {html.escape(icon['apple'])}</span>"
            f"<span>Windows · {html.escape(icon['windows'])}</span>"
            f"<span>Android · {html.escape(icon['android'])}"
            f"{' · FILL 1' if entry['androidFill'] else ''}</span></div>"
        )
        rows.append(
            f"<tr data-search='{html.escape(icon['id'] + ' ' + icon['title'])}'><td class='semantic'><b>{html.escape(icon['title'])}</b><code>{icon['id']}</code>{names}</td>"
            f"<td colspan='3'>{samples}</td></tr>"
        )
    document = """<!doctype html><html lang='zh-CN'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>PhotoViewer 语义图标对比</title><style>
__FONTS__
:root{color-scheme:dark}*{box-sizing:border-box}body{margin:24px;background:#111214;color:#f2f2f2;font:14px -apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif}header{display:flex;align-items:center;gap:20px;margin-bottom:20px}h1{font-size:20px;margin:0}input{width:min(420px,60vw);padding:9px 12px;border:1px solid #45464b;border-radius:8px;background:#1c1d20;color:inherit}table{width:100%;border-collapse:collapse}th,td{text-align:left;border-bottom:1px solid #2b2c30;padding:12px 10px;vertical-align:middle}.group th{padding-top:26px;color:#a9abb2;font-size:13px}.semantic{width:38%;min-width:310px}.semantic b{display:block;margin-bottom:4px}.semantic code{display:block;color:#9ea1a9}.names{display:flex;flex-direction:column;color:#777b85;font:11px ui-monospace,monospace;margin-top:8px;gap:2px}.sizes{display:flex;gap:30px}.sample{text-align:center}.sample small{display:block;color:#777b85;margin-top:4px}.triple{display:grid;grid-template-columns:repeat(3,48px);align-items:center;justify-items:center;min-height:52px}.triple>div{width:48px;height:48px;display:grid;place-items:center;border-left:1px solid #26272b}.triple>div:last-child{border-right:1px solid #26272b}.glyph{font-family:pv-fluent-regular;line-height:1}.fluent-filled{font-family:pv-fluent-filled}.material-regular{font-family:pv-material-regular}.material-filled{font-family:pv-material-filled}img{max-width:32px;max-height:32px;filter:brightness(0) invert(1)}.missing{color:#ff7b72;font-size:11px}@media(max-width:800px){.semantic{min-width:220px}.names{display:none}.sizes{gap:8px}.triple{grid-template-columns:repeat(3,36px)}.triple>div{width:36px}}
</style></head><body><header><h1>PhotoViewer 语义图标对比</h1><input id='filter' placeholder='搜索语义 ID 或中文名称'></header>
<div>每行从左到右：Apple 实际系统渲染 / Windows Fluent / Android Material Rounded；每档为 18 / 24 点。</div>
<table>__ROWS__</table><script>document.getElementById('filter').addEventListener('input',e=>{const q=e.target.value.toLowerCase();document.querySelectorAll('tr[data-search]').forEach(r=>r.hidden=!r.dataset.search.toLowerCase().includes(q))})</script></body></html>"""
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(document.replace("__FONTS__", "\n".join(font_css)).replace("__ROWS__", "\n".join(rows)), encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--refresh", action="store_true", help="重新下载固定上游并重建字体")
    parser.add_argument("--check", action="store_true", help="只检查生成物，不写文件")
    parser.add_argument("--no-apple", action="store_true", help="非 macOS 环境只生成 Windows / Android 对比")
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
            print(f"PASS {len(source['icons'])} 个语义图标；码位、字体摘要与生成码位表一致")
        return 1 if errors else 0
    manifest = refresh(source) if args.refresh or not MANIFEST.exists() else read_json(MANIFEST)
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
