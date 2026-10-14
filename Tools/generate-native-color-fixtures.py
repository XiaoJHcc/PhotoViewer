"""生成 P2 色彩与方向回归图，ICC 来自本机系统配置，不依赖网络。"""

from pathlib import Path
import argparse
from PIL import Image, ImageDraw


def generate(destination: Path) -> None:
    """创建带不同 RGB profile 的相同色块、灰阶、非对称方向标记图。"""
    destination.mkdir(parents=True, exist_ok=True)
    colors = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (0, 255, 255), (255, 0, 255), (255, 255, 0)]
    image = Image.new("RGB", (1200, 800), (64, 64, 64))
    draw = ImageDraw.Draw(image)
    for index, color in enumerate(colors):
        horizontal = index % 3 * 400
        vertical = index // 3 * 280
        draw.rectangle((horizontal, vertical, horizontal + 399, vertical + 279), fill=color)
    for horizontal in range(1200):
        gray = round(horizontal * 255 / 1199)
        draw.line((horizontal, 560, horizontal, 719), fill=(gray, gray, gray))
    draw.rectangle((0, 720, 180, 799), fill=(255, 255, 255))
    draw.text((16, 738), "TOP / LEFT", fill=(0, 0, 0))
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


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("destination", type=Path)
    arguments = parser.parse_args()
    generate(arguments.destination)
    print(f"Generated 12 ICC/orientation fixtures in {arguments.destination}")
