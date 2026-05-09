#!/usr/bin/env python3
"""
Tools/export_dinov3_onnx.py

将 HuggingFace 上的 DINOv3 ViT-S/16 导出为 ONNX，仅取 [CLS] token 作为图像特征。
产物用于 PhotoViewer 端侧特征提取（A2-M1）。

用法:
    python Tools/export_dinov3_onnx.py \
        --model-id facebook/dinov3-vits16-pretrain-lvd1689m \
        --output PhotoViewer/Assets/Models/dinov3_vits16.onnx

依赖:
    pip install torch transformers onnx onnxruntime pillow numpy

备注:
- 默认输入分辨率 518×518（DINOv3 官方推理规格），dynamic_axes 只放开 batch 维度。
- 仅导出 [CLS] 向量（last_hidden_state[:, 0, :]），Patch tokens 不要。
- DINOv3 权重受 Meta DINOv3 License 约束，打包分发前需 review。
"""

from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

import torch
from transformers import AutoModel


class DinoClsWrapper(torch.nn.Module):
    """只输出 [CLS] token 的包装，简化 ONNX 图。"""

    def __init__(self, backbone: torch.nn.Module) -> None:
        super().__init__()
        self.backbone = backbone

    def forward(self, pixel_values: torch.Tensor) -> torch.Tensor:
        out = self.backbone(pixel_values=pixel_values)
        # last_hidden_state: [B, num_tokens, dim]，第 0 个是 [CLS]
        return out.last_hidden_state[:, 0, :]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Export DINOv3 ViT backbone [CLS] to ONNX")
    parser.add_argument(
        "--model-id",
        default="facebook/dinov3-vits16-pretrain-lvd1689m",
        help="HuggingFace repo id (S/B/L 均可)",
    )
    parser.add_argument(
        "--output",
        default="PhotoViewer/Assets/Models/dinov3_vits16.onnx",
        help="目标 ONNX 文件路径（相对仓库根目录）",
    )
    parser.add_argument("--image-size", type=int, default=518, help="方形输入边长")
    parser.add_argument("--opset", type=int, default=17)
    parser.add_argument("--fp16", action="store_true", help="导出 FP16（端侧包体更小）")
    return parser.parse_args()


def main() -> int:
    args = parse_args()

    out_path = Path(args.output)
    out_path.parent.mkdir(parents=True, exist_ok=True)

    print(f"[export] loading {args.model_id}", flush=True)
    model = AutoModel.from_pretrained(args.model_id).eval()

    if args.fp16:
        model = model.half()
        dtype = torch.float16
    else:
        dtype = torch.float32

    wrapper = DinoClsWrapper(model).eval()
    dummy = torch.randn(1, 3, args.image_size, args.image_size, dtype=dtype)

    print(f"[export] exporting to {out_path} (opset={args.opset}, dtype={dtype})", flush=True)
    # external_data=False：把所有权重内嵌到单个 .onnx 文件，Avalonia 作为资源加载只需一份文件。
    # dynamic_shapes 用于 torch 2.x dynamo 导出器放开 batch 维。
    with torch.no_grad():
        torch.onnx.export(
            wrapper,
            dummy,
            str(out_path),
            input_names=["pixel_values"],
            output_names=["cls_embedding"],
            opset_version=args.opset,
            dynamic_shapes={"pixel_values": {0: torch.export.Dim("batch")}},
            do_constant_folding=True,
            external_data=False,
        )

    size_mb = os.path.getsize(out_path) / (1024 * 1024)
    print(f"[export] done: {out_path} ({size_mb:.1f} MB)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
