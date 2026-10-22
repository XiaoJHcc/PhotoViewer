"""生成 P2 色彩与方向回归图，ICC 来自本机系统配置，不依赖网络。"""

from pathlib import Path
import argparse
from PIL import Image, ImageDraw

# 色块布局（1200×800 基准）：上两行六色块、中间灰阶、左下白色方向标记
BASE_SIZE = (1200, 800)
COLORS = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (0, 255, 255), (255, 0, 255), (255, 255, 0)]


def _chart(size: tuple[int, int], scale: float) -> Image.Image:
    """按基准布局（×scale）画出色块、灰阶与非对称方向标记图。"""
    image = Image.new("RGB", size, (64, 64, 64))
    draw = ImageDraw.Draw(image)
    for index, color in enumerate(COLORS):
        horizontal = round(index % 3 * 400 * scale)
        vertical = round(index // 3 * 280 * scale)
        draw.rectangle((horizontal, vertical, round(horizontal + 399 * scale), round(vertical + 279 * scale)), fill=color)
    gray_top = round(560 * scale)
    gray_bottom = round(719 * scale)
    width = round(1199 * scale)
    for horizontal in range(round(1200 * scale)):
        gray = round(horizontal * 255 / width)
        draw.line((horizontal, gray_top, horizontal, gray_bottom), fill=(gray, gray, gray))
    draw.rectangle((0, round(720 * scale), round(180 * scale), round(799 * scale)), fill=(255, 255, 255))
    draw.text((round(16 * scale), round(738 * scale)), "TOP / LEFT", fill=(0, 0, 0))
    return image


def generate(destination: Path, large_output: Path | None = None) -> None:
    """创建带不同 RGB profile 的相同色块、灰阶、非对称方向标记图。"""
    destination.mkdir(parents=True, exist_ok=True)
    image = _chart(BASE_SIZE, 1.0)
    profiles = Path("/System/Library/ColorSync/Profiles")
    rating_packet = b'<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmp:Rating="0"/></rdf:RDF></x:xmpmeta>'
    for name, profile in [("srgb", "sRGB Profile.icc"), ("p3", "Display P3.icc"), ("adobe", "AdobeRGB1998.icc")]:
        image.save(destination / f"color-{name}.jpg", quality=100, subsampling=0, icc_profile=(profiles / profile).read_bytes(), xmp=rating_packet)
    for orientation in range(1, 9):
        metadata = Image.Exif()
        metadata[274] = orientation
        image.save(destination / f"orientation-{orientation}.jpg", quality=100, subsampling=0, exif=metadata, icc_profile=(profiles / "sRGB Profile.icc").read_bytes(), xmp=rating_packet)
    metadata = Image.Exif()
    metadata[34665] = {40961: 2}
    image.save(destination / "color-untagged-adobe.jpg", quality=100, subsampling=0, exif=metadata, xmp=rating_packet)
    if large_output is not None:
        # 6000×4000 Display P3 色卡：长边 ≥ 4000 才能让 iPad 的 2× 视口代理档成立（--pv-native-photo-check 必需素材）。
        # 必须放在独立目录：Mac 回归会枚举 release/native-p2-fixtures 并只接受已登记的 color-* 名称。
        large_output.mkdir(parents=True, exist_ok=True)
        scale = 5.0
        size = (round(BASE_SIZE[0] * scale), round(BASE_SIZE[1] * scale))
        _chart(size, scale).save(large_output / "color-p3-large.jpg", quality=100, subsampling=0,
                                 icc_profile=(profiles / "Display P3.icc").read_bytes(), xmp=rating_packet)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("destination", type=Path)
    parser.add_argument("--large-output", type=Path, default=None,
                        help="额外把 color-p3-large.jpg（6000×4000 Display P3）写到该目录，供 --pv-native-photo-check")
    arguments = parser.parse_args()
    generate(arguments.destination, arguments.large_output)
    print(f"Generated 12 ICC/orientation fixtures in {arguments.destination}"
          + (f" + color-p3-large.jpg in {arguments.large_output}" if arguments.large_output else ""))
