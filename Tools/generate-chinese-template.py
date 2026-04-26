#!/usr/bin/env python3
"""
Tools/generate-chinese-template.py

生成 PhotoViewer/Core/Exif/ExifChinese.cs —— EXIF 字段中文名称映射表。

工作流程：
  1. 首次运行：生成包含已知中文翻译 + 注释掉的未知条目的完整模板
    2. 手动编辑 Exif/ExifChinese.cs：取消注释并填写中文名称
  3. 如需加入新 ExifTool 模块的条目，可重新运行脚本，再用 git diff 合并改动

用法:
    python3 Tools/generate-chinese-template.py [--ref <git-ref>]

参数:
    --ref   ExifTool GitHub 仓库的 git ref（默认 master）

输出:
    PhotoViewer/Core/Exif/ExifChinese.cs  （首次生成后由用户手动维护）
"""

import re
import sys
import urllib.request
import urllib.error
from datetime import datetime, timezone
from pathlib import Path
from argparse import ArgumentParser
from typing import Optional

EXIFTOOL_RAW = "https://raw.githubusercontent.com/exiftool/exiftool/{ref}/lib/Image/ExifTool/{file}.pm"

REPO_ROOT = Path(__file__).parent.parent
OUTPUT_PATH = REPO_ROOT / "PhotoViewer" / "Core" / "Exif" / "ExifChinese.Generated.cs"

# ============================================================
#  A. MetadataExtractor 格式（Title Case With Spaces / 特殊符号）
#     这些 tag 已被 MetadataExtractor 识别，不走 ExifTool 补充路径。
#     键名须与 MetadataExtractor tag.Name 字段完全一致。
# ============================================================
KNOWN_STANDARD = {
    # ---- 文件信息 ----
    "Detected File Type Name":          "文件类型",
    "Detected File Type Long Name":     "文件类型全称",
    "Detected MIME Type":               "MIME 类型",
    "Expected File Name Extension":     "文件扩展名",

    # ---- IFD0 基本信息 ----
    "Make":                             "制造商",
    "Model":                            "相机型号",
    "Image Description":                "图像描述",
    "Orientation":                      "图像朝向",
    "X Resolution":                     "X 分辨率",
    "Y Resolution":                     "Y 分辨率",
    "Resolution Unit":                  "分辨率单位",
    "Software":                         "软件版本",
    "Date/Time":                        "修改时间",
    "Artist":                           "作者",
    "Copyright":                        "版权",
    "YCbCr Positioning":                "YCbCr 定位",
    "Subfile Type":                     "子文件类型",
    "Compression":                      "压缩方式",
    "Strip Offsets":                    "数据条偏移",
    "Rows Per Strip":                   "每条行数",
    "Strip Byte Counts":                "数据条字节计数",
    "Planar Configuration":             "平面配置",
    "Image Width":                      "图像宽",
    "Image Height":                     "图像高",
    "Bits Per Sample":                  "位深度",
    "Samples Per Pixel":                "每像素样本数",
    "Photometric Interpretation":       "光度解释",
    "White Point":                      "白点",
    "Primary Chromaticities":           "主色度",

    # ---- ExifSubIFD 拍摄参数 ----
    "Exposure Time":                    "曝光时间",
    "F-Number":                         "光圈",
    "Exposure Program":                 "曝光程序",
    "Spectral Sensitivity":             "光谱灵敏度",
    "ISO Speed Ratings":                "ISO 感光度",
    "Sensitivity Type":                 "感光度类型",
    "Standard Output Sensitivity":      "标准输出感光度",
    "Recommended Exposure Index":       "推荐曝光指数",
    "ISO Speed":                        "ISO 速度",
    "Exif Version":                     "EXIF 版本",
    "Date/Time Original":               "拍摄时间",
    "Date/Time Digitized":              "数字化时间",
    "Components Configuration":         "颜色分量配置",
    "Compressed Bits Per Pixel":        "压缩比特/像素",
    "Shutter Speed Value":              "快门速度值",
    "Aperture Value":                   "光圈值",
    "Brightness Value":                 "亮度值",
    "Exposure Bias Value":              "曝光补偿",
    "Max Aperture Value":               "最大光圈",
    "Subject Distance":                 "主体距离",
    "Metering Mode":                    "测光模式",
    "Light Source":                     "光源",
    "Flash":                            "闪光灯",
    "Focal Length":                     "焦距",
    "Subject Area":                     "主体区域",
    "Flashpix Version":                 "Flashpix 版本",
    "Color Space":                      "色彩空间",
    "Exif Image Width":                 "图像宽（像素）",
    "Exif Image Height":                "图像高（像素）",
    "Related Sound File":               "关联音频文件",
    "Flash Energy":                     "闪光能量",
    "Focal Plane X Resolution":         "焦平面 X 分辨率",
    "Focal Plane Y Resolution":         "焦平面 Y 分辨率",
    "Focal Plane Resolution Unit":      "焦平面分辨率单位",
    "Subject Location":                 "主体位置",
    "Exposure Index":                   "曝光指数",
    "Sensing Method":                   "感光方式",
    "File Source":                      "文件来源",
    "Scene Type":                       "场景类型",
    "CFA Pattern":                      "CFA 模式",
    "Custom Rendered":                  "自定义渲染",
    "Exposure Mode":                    "曝光模式",
    "White Balance Mode":               "白平衡模式",
    "Digital Zoom Ratio":               "数码变焦比",
    "35mm Film Equivalent Focal Length": "35mm 等效焦距",
    "Scene Capture Type":               "场景拍摄类型",
    "Gain Control":                     "增益控制",
    "Contrast":                         "对比度",
    "Saturation":                       "饱和度",
    "Sharpness":                        "锐度",
    "Subject Distance Range":           "主体距离范围",
    "Unique Image ID":                  "图像唯一 ID",
    "Camera Owner Name":                "相机所有者",
    "Body Serial Number":               "机身序列号",
    "Lens Specification":               "镜头规格",
    "Lens Make":                        "镜头制造商",
    "Lens Model":                       "镜头型号",
    "Lens Serial Number":               "镜头序列号",
    "Gamma":                            "伽马值",
    "White Balance":                    "白平衡",
    "User Comment":                     "用户注释",
    "Maker Note":                       "制造商备注",
    "Sub-Sec Time":                     "亚秒时间",
    "Sub-Sec Time Original":            "拍摄亚秒时间",
    "Sub-Sec Time Digitized":           "数字化亚秒时间",

    # ---- GPS ----
    "GPS Version ID":                   "GPS 版本",
    "GPS Latitude Ref":                 "纬度方向",
    "GPS Latitude":                     "纬度",
    "GPS Longitude Ref":                "经度方向",
    "GPS Longitude":                    "经度",
    "GPS Altitude Ref":                 "海拔参考",
    "GPS Altitude":                     "海拔",
    "GPS Time-Stamp":                   "GPS 时间",
    "GPS Satellites":                   "GPS 卫星",
    "GPS Status":                       "GPS 状态",
    "GPS Measure Mode":                 "GPS 测量模式",
    "GPS DOP":                          "GPS 精度",
    "GPS Speed Ref":                    "GPS 速度单位",
    "GPS Speed":                        "GPS 速度",
    "GPS Track Ref":                    "航向参考",
    "GPS Track":                        "运动航向",
    "GPS Img Direction Ref":            "图像方向参考",
    "GPS Img Direction":                "图像朝向",
    "GPS Map Datum":                    "地图基准",
    "GPS Dest Latitude Ref":            "目的地纬度方向",
    "GPS Dest Latitude":                "目的地纬度",
    "GPS Dest Longitude Ref":           "目的地经度方向",
    "GPS Dest Longitude":               "目的地经度",
    "GPS Dest Bearing Ref":             "目的地方位参考",
    "GPS Dest Bearing":                 "目的地方位",
    "GPS Dest Distance Ref":            "目的地距离单位",
    "GPS Dest Distance":                "目的地距离",
    "GPS Processing Method":            "GPS 处理方法",
    "GPS Area Information":             "GPS 区域信息",
    "GPS Date Stamp":                   "GPS 日期",
    "GPS Differential":                 "GPS 差分修正",
    "GPS H Positioning Error":          "GPS 水平定位误差",

    # ---- IPTC ----
    "Application Record Version":       "应用记录版本",
    "Caption/Abstract":                 "图片说明",
    "Writer/Editor":                    "说明作者",
    "Headline":                         "标题行",
    "Special Instructions":             "特殊说明",
    "By-line":                          "作者",
    "By-line Title":                    "作者职称",
    "Credit":                           "来源",
    "Source":                           "来源机构",
    "Object Name":                      "对象名称",
    "Date Created":                     "创建日期",
    "City":                             "城市",
    "Sub-location":                     "子地点",
    "Province/State":                   "省/州",
    "Country/Primary Location Code":    "国家代码",
    "Country/Primary Location Name":    "国家",
    "Original Transmission Reference":  "传输参考",
    "Category":                         "分类",
    "Keywords":                         "关键词",
    "Copyright Notice":                 "版权声明",
    "Coded Character Set":              "字符集编码",
    "Time Created":                     "创建时间",
    "Digital Date Created":             "数字化日期",
    "Digital Time Created":             "数字化时间",
    "Unique Document ID":               "文档唯一 ID",

    # ---- ICC Profile ----
    "Profile Size":                     "配置文件大小",
    "CMM Type":                         "CMM 类型",
    "Version":                          "版本",
    "Class":                            "配置文件类型",
    "Profile Connection Space":         "配置文件连接空间",
    "Profile Date/Time":                "配置文件日期",
    "Signature":                        "签名",
    "Primary Platform":                 "主平台",
    "Device Manufacturer":              "设备制造商",
    "Device Model":                     "设备型号",
    "Media White Point":                "媒体白点",
    "Profile Description":              "配置描述",
    "Red Colorant":                     "红色颜色剂",
    "Green Colorant":                   "绿色颜色剂",
    "Blue Colorant":                    "蓝色颜色剂",
    "Red TRC":                          "红色色调曲线",
    "Green TRC":                        "绿色色调曲线",
    "Blue TRC":                         "蓝色色调曲线",
    "Rendering Intent":                 "渲染意图",
}


# ============================================================
#  B. ExifTool 格式（CamelCase）
#     这些 tag 仅当 MetadataExtractor 无法识别时才会出现（走 ExifTool 补充路径）。
#     键名须与 ExifTool .pm 文件中 Name => '...' 的值完全一致。
# ============================================================
KNOWN_VENDOR = {
    # ---- Sony ----
    "DriveMode":                        "驱动模式",
    "FocusMode":                        "对焦模式",
    "AFAreaMode":                       "自动对焦区域",
    "AFIlluminator":                    "自动对焦辅助灯",
    "AFStatus":                         "自动对焦状态",
    "AFMicroAdjValue":                  "对焦微调值",
    "AFMicroAdjOn":                     "对焦微调启用",
    "FlexibleSpotPosition":             "灵活点对焦位置",
    "AFPointSelected":                  "已选对焦点",
    "Quality":                          "图像质量",
    "CreativeStyle":                    "创意风格",
    "ColorTemperature":                 "色温",
    "WBShiftAB":                        "白平衡偏移（琥珀/蓝）",
    "WBShiftMB":                        "白平衡偏移（品红/绿）",
    "DynamicRangeOptimizer":            "动态范围优化",
    "AutoHDR":                          "自动 HDR",
    "HDR":                              "HDR",
    "PictureEffect":                    "画面效果",
    "PictureProfile":                   "图片配置",
    "NoiseReduction":                   "降噪",
    "LongExposureNoiseReductionOn":     "长曝光降噪",
    "HighISONoiseReductionOn":          "高 ISO 降噪",
    "MultiFrameNoiseReduction":         "多帧降噪",
    "SteadyShot":                       "光学防抖",
    "FocalDistance":                    "对焦距离",
    "FocusStatus":                      "对焦状态",
    "SceneMode":                        "场景模式",
    "Rotation":                         "旋转",
    "LensType":                         "镜头类型",
    "LensID":                           "镜头 ID",
    "LensSpec":                         "镜头规格",
    "SonyISO":                          "感光度（索尼）",
    "SonyExposureTime":                 "快门速度（索尼）",
    "SonyFNumber":                      "光圈（索尼）",
    "MakerNoteVersion":                 "制造商备注版本",
    "SonyDateTime":                     "拍摄时间（索尼）",
    "SonyImageSize":                    "图像尺寸（索尼）",
    "FaceDetection":                    "人脸检测",
    "SmileShutter":                     "微笑快门",
    "SweepPanoramaDirection":           "全景扫描方向",
    "SweepPanoramaFieldOfView":         "全景视角",
    "SweepPanoramaSize":                "全景尺寸",
    "WhiteBalanceBiasA":                "白平衡偏移（琥珀）",
    "WhiteBalanceBiasB":                "白平衡偏移（蓝）",
    "ExposureMode":                     "曝光模式",
    "FlashExposureComp":                "闪光曝光补偿",
    "Teleconverter":                    "增距镜",
    "MeteringMode":                     "测光模式",
    "ShutterCount":                     "快门次数",
    "SequenceImageNumber":              "连拍序号",
    "SequenceLength":                   "连拍长度",
    "ModelReleaseYear":                 "型号发布年份",
    "Format":                           "格式",

    # ---- Nikon ----
    "ShutterCount":                     "快门次数",
    "ActiveDLighting":                  "主动 D-Lighting",
    "VRInfo":                           "防抖信息",
    "VibrationReduction":               "防抖",
    "DriveMode":                        "驱动模式",
    "FocusMode":                        "对焦模式",
    "AFAreaMode":                       "自动对焦区域",
    "AFInfo2":                          "对焦信息",
    "FlashMode":                        "闪光模式",
    "FlashSync":                        "闪光同步",
    "FlashExposureComp":                "闪光曝光补偿",
    "ExposureBracketValue":             "曝光包围值",
    "PictureControlName":               "相片风格",
    "Sharpness":                        "锐度",
    "Contrast":                         "对比度",
    "Brightness":                       "亮度",
    "Saturation":                       "饱和度",
    "HueAdjustment":                    "色调调整",
    "FilterEffect":                     "滤镜效果",
    "ToningEffect":                     "色调效果",
    "HighISONoiseReduction":            "高 ISO 降噪",
    "VignetteControl":                  "暗角控制",
    "DistortionControl":                "畸变控制",
    "MultiExposure":                    "多重曝光",
    "RetouchHistory":                   "修饰历史",
    "SerialNumber":                     "序列号",
    "NikonISO":                         "感光度（尼康）",
    "ColorMode":                        "色彩模式",
    "ImageAdjustment":                  "图像调整",

    # ---- Canon ----
    "MacroMode":                        "微距模式",
    "SelfTimer":                        "自拍计时器",
    "Quality":                          "图像质量",
    "CanonFlashMode":                   "闪光模式",
    "ContinuousDrive":                  "连拍模式",
    "FocusMode":                        "对焦模式",
    "CanonExposureMode":                "曝光模式",
    "MaxFocalLength":                   "最大焦距",
    "MinFocalLength":                   "最小焦距",
    "ImageStabilization":               "图像防抖",
    "AFPointsInFocus":                  "合焦对焦点",
    "CanonModelID":                     "相机型号 ID",
    "FileNumber":                       "文件编号",
    "WhiteBalance":                     "白平衡",
    "SequenceNumber":                   "序列编号",
    "FlashGuideNumber":                 "闪光指数",
    "CameraTemperature":                "相机温度",
    "FlashExposureComp":                "闪光曝光补偿",
    "AutoExposureBracketing":           "自动曝光包围",
    "BulbDuration":                     "B 门时长",
    "AutoRotate":                       "自动旋转",
    "NDFilter":                         "ND 滤镜",
    "SerialNumber":                     "序列号",
    "ColorTemperature":                 "色温",

    # ---- Fujifilm ----
    "FilmMode":                         "胶片模式",
    "DynamicRange":                     "动态范围",
    "DynamicRangeSetting":              "动态范围设置",
    "ColorSaturation":                  "色彩饱和度",
    "Sharpness":                        "锐度",
    "ShadowTone":                       "阴影色调",
    "HighlightTone":                    "高光色调",
    "AutoISO":                          "自动 ISO",
    "BaseISO":                          "基础 ISO",
    "BlurWarning":                      "模糊警告",
    "FocusWarning":                     "对焦警告",
    "ExposureWarning":                  "曝光警告",
    "ShutterType":                      "快门类型",
    "PictureMode":                      "拍摄模式",
    "SceneRecognition":                 "场景识别",
    "NoiseReduction":                   "降噪",
    "CameraOrientation":                "相机朝向",
    "FujiFlashMode":                    "闪光模式",
    "FlashExposureComp":                "闪光曝光补偿",
    "SerialNumber":                     "序列号",
    "Rating":                           "评分",

    # ---- Olympus / OM System ----
    "SpecialMode":                      "特殊模式",
    "JPEGQuality":                      "JPEG 质量",
    "Macro":                            "微距",
    "BWMode":                           "黑白模式",
    "DigitalZoom":                      "数码变焦",
    "SceneMode":                        "场景模式",
    "SerialNumber":                     "序列号",
    "Firmware":                         "固件版本",
    "PreCaptureFrames":                 "预捕获帧数",
    "FaceDetectArea":                   "人脸检测区域",

    # ---- Panasonic ----
    "Quality":                          "图像质量",
    "FirmwareVersion":                  "固件版本",
    "WhiteBalance":                     "白平衡",
    "FocusMode":                        "对焦模式",
    "AFMode":                           "自动对焦模式",
    "ImageStabilization":               "图像防抖",
    "Macro":                            "微距",
    "AudioZoom":                        "录音变焦",
    "OpticalZoom":                      "光学变焦",
    "NoiseReduction":                   "降噪",
    "SelfTimer":                        "自拍计时器",
    "Rotation":                         "旋转",
    "AFAssist":                         "自动对焦辅助",
    "ColorEffect":                      "色彩效果",
    "BurstMode":                        "连拍模式",
    "SequenceNumber":                   "序列编号",
    "ContrastMode":                     "对比度模式",
    "SharpnessLevel":                   "锐度等级",
    "FlashBias":                        "闪光偏置",
    "ExternalFlashStrength":            "外置闪光强度",
    "IntelligentExposure":              "智能曝光",
    "FlashCurtain":                     "闪光幕帘",
    "LensTemperature":                  "镜头温度",
    "PanasonicISO":                     "感光度（松下）",
    "SerialNumber":                     "序列号",
    "IntelligentDRange":                "智能动态范围",
    "ClearRetouch":                     "清晰修饰",
    "LevelOrientation":                 "水平方向",
    "SceneMode":                        "场景模式",

    # ---- Pentax ----
    "PentaxModelID":                    "机型 ID",
    "Date":                             "日期",
    "Time":                             "时间",
    "Quality":                          "图像质量",
    "FocusMode":                        "对焦模式",
    "FlashMode":                        "闪光模式",
    "DriveMode":                        "驱动模式",
    "WhiteBalance":                     "白平衡",
    "Sharpness":                        "锐度",
    "Contrast":                         "对比度",
    "Saturation":                       "饱和度",
    "NoiseReduction":                   "降噪",
    "FlashExposureComp":                "闪光曝光补偿",
    "ImageStabilization":               "图像防抖",
    "SerialNumber":                     "序列号",
    "LevelOrientation":                 "水平方向",
    "FaceDetect":                       "人脸检测",

    # ---- Apple ----
    "ImageCaptureType":                 "拍摄类型",
    "ImageUniqueID":                    "图像唯一 ID",
    "AccelerationVector":               "加速度向量",
    "HDRHeadroom":                      "HDR 余量",
    "FocusDistanceRange":               "对焦距离范围",

    # ---- DJI ----
    "Make":                             "制造商",
    "SpeedX":                           "X 轴速度",
    "SpeedY":                           "Y 轴速度",
    "SpeedZ":                           "Z 轴速度",
    "Pitch":                            "俯仰角",
    "Yaw":                              "偏航角",
    "Roll":                             "横滚角",
    "CameraISO":                        "感光度",
    "CameraExposureTime":               "曝光时间",
    "CameraFNumber":                    "光圈",
    "SerialNumber":                     "序列号",
    "FlightXSpeed":                     "飞行 X 速度",
    "FlightYSpeed":                     "飞行 Y 速度",
    "FlightZSpeed":                     "飞行 Z 速度",
    "FlightPitchDegree":                "飞行俯仰角",
    "FlightYawDegree":                  "飞行偏航角",
    "FlightRollDegree":                 "飞行横滚角",
    "GimbalXDegree":                    "云台 X 角度",
    "GimbalYDegree":                    "云台 Y 角度",
    "GimbalZDegree":                    "云台 Z 角度",
}


# ============================================================
#  ExifTool 模块抓取
# ============================================================
MODULES = [
    ("Sony",      "Sony"),
    ("Nikon",     "Nikon"),
    ("Canon",     "Canon"),
    ("Fujifilm",  "FujiFilm"),
    ("Panasonic", "Panasonic"),
    ("Olympus",   "Olympus"),
    ("Pentax",    "Pentax"),
    ("Sigma",     "Sigma"),
    ("Minolta",   "Minolta"),
    ("Samsung",   "Samsung"),
    ("Apple",     "Apple"),
    ("Reconyx",   "Reconyx"),
    ("Ricoh",     "Ricoh"),
    ("Casio",     "Casio"),
    ("Kodak",     "Kodak"),
    ("DJI",       "DJI"),
    ("Kyocera",   "KyoceraRaw"),
    ("Sanyo",     "Sanyo"),
    ("FLIR",      "FLIR"),
]

_TAG_ID_RE = re.compile(r'^\s+(0x[0-9a-fA-F]{1,6}|\b\d{1,5}\b)\s*=>')
_NAME_RE   = re.compile(r'Name\s*=>\s*[\'"]([A-Za-z][A-Za-z0-9_]{1,80})[\'"]')
_INLINE_STR_RE = re.compile(r"^['\"]([A-Za-z][A-Za-z0-9_]{1,80})['\"]")


def fetch_pm(file_name: str, ref: str) -> Optional[str]:
    url = EXIFTOOL_RAW.format(ref=ref, file=file_name)
    try:
        with urllib.request.urlopen(url, timeout=30) as resp:
            return resp.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as e:
        print(f"  [SKIP] HTTP {e.code}: {file_name}.pm", file=sys.stderr)
        return None
    except Exception as e:
        print(f"  [ERROR] {file_name}.pm: {e}", file=sys.stderr)
        return None


def extract_tag_names(content: str):
    """从 ExifTool .pm 文件提取所有 tag name（保持顺序，去重）。"""
    seen = set()
    names = []
    lines = content.split('\n')
    n = len(lines)
    for i, line in enumerate(lines):
        m = _TAG_ID_RE.match(line)
        if not m:
            continue
        rest = line[m.end():].strip()
        name = None
        sm = _INLINE_STR_RE.match(rest)
        if sm:
            name = sm.group(1)
        elif rest.startswith('{'):
            for j in range(i + 1, min(i + 25, n)):
                nm = _NAME_RE.search(lines[j])
                if nm:
                    name = nm.group(1)
                    break
                if _TAG_ID_RE.match(lines[j]):
                    break
        if name and name not in seen:
            seen.add(name)
            names.append(name)
    return names


def _escape(s: str) -> str:
    return s.replace('\\', '\\\\').replace('"', '\\"')


def _cs_entry(tag_name: str, chinese: Optional[str], indent: str = '        ') -> str:
    escaped = _escape(tag_name)
    if chinese:
        return f'{indent}["{escaped}"] = "{_escape(chinese)}",'
    else:
        return f'{indent}// ["{escaped}"] = "",'


def generate_cs(module_tag_names, ref: str) -> str:
    now = datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M UTC')

    lines = [
        "// PhotoViewer/Core/Exif/ExifChinese.Generated.cs",
        "// 【自动生成文件，请勿直接编辑】",
        "//",
        "// 如需修改中文名称，请在 ExifChinese.cs 的 _overrideNames 中添加覆盖条目。",
        "// 脚本每次运行会完全覆盖本文件，而 ExifChinese.cs 永远不会被脚本修改。",
        "",
        f"// 生成命令：python3 Tools/generate-chinese-template.py  (ExifTool ref: {ref})",
        f"// 生成时间：{now}",
        "",
        "using System;",
        "using System.Collections.Generic;",
        "",
        "namespace PhotoViewer.Core;",
        "",
        "/// <summary>",
        "/// EXIF 字段中文名称映射表（自动生成部分）。",
        "/// 请在 ExifChinese.cs 的 _overrideNames 中添加手动覆盖条目。",
        "/// 更新命令：python3 Tools/generate-chinese-template.py",
        "/// </summary>",
        "internal static partial class ExifChinese",
        "{",
        "    private static readonly IReadOnlyDictionary<string, string> _generatedNames =",
        "        new Dictionary<string, string>(StringComparer.Ordinal)",
        "        {",
        "            // ======== 标准 EXIF / GPS / IPTC / ICC ========",
    ]

    for tag_name, chinese in sorted(KNOWN_STANDARD.items(), key=lambda x: x[0]):
        lines.append(_cs_entry(tag_name, chinese, '            '))

    for module_key, tag_names in module_tag_names:
        lines.append(f"")
        lines.append(f"            // ======== {module_key} Makernote ========")
        for name in tag_names:
            cn = KNOWN_VENDOR.get(name)
            lines.append(_cs_entry(name, cn, '            '))

    lines += [
        "        };",
        "}",
        "",
    ]
    return '\n'.join(lines)


def main() -> None:
    parser = ArgumentParser(description="生成 Exif/ExifChinese.cs EXIF 字段中文名称模板")
    parser.add_argument('--ref', default='master',
                        help='ExifTool GitHub 仓库的 git ref（默认: master）')
    args = parser.parse_args()
    ref = args.ref  # type: str

    print(f"ExifTool ref: {ref}")
    print(f"输出路径: {OUTPUT_PATH.relative_to(REPO_ROOT)}\n")

    module_tag_names = []  # type: list
    for module_key, file_name in MODULES:
        print(f"  Fetching {file_name}.pm ...", end=' ', flush=True)
        content = fetch_pm(file_name, ref)
        if content is None:
            continue
        names = extract_tag_names(content)
        module_tag_names.append((module_key, names))
        cn_count = sum(1 for n in names if n in KNOWN_VENDOR)
        print(f"{len(names)} tags，其中 {cn_count} 个有中文名")

    cs = generate_cs(module_tag_names, ref)
    OUTPUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT_PATH.write_text(cs, encoding='utf-8')

    total_tags = sum(len(t) for _, t in module_tag_names)
    total_cn = len(KNOWN_STANDARD) + len(KNOWN_VENDOR)
    print(f"\n✓ 生成完成：{len(module_tag_names)} 个厂商模块，{total_tags} 条厂商 tag")
    print(f"  标准 EXIF 中文条目: {len(KNOWN_STANDARD)}")
    print(f"  厂商 tag 有中文名: 约 {len(KNOWN_VENDOR)} 条（含跨品牌同名 tag）")
    print(f"  注意：生成后请用 git diff 合并此前手动编辑的内容")


if __name__ == '__main__':
    main()
