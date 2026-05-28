#!/usr/bin/env python3
"""
Tools/export_dinov3_onnx.py

将 HuggingFace 上的 DINOv3 ViT 导出为 ONNX，双输出（CLS + patch tokens）。
产物用于 PhotoViewer 端侧特征提取（A2-M1）。

用法:
    python Tools/export_dinov3_onnx.py \
        --model-id facebook/dinov3-vits16-pretrain-lvd1689m \
        --output PhotoViewer/Assets/Models/dinov3_vits16.onnx

依赖:
    pip install torch transformers onnx onnxruntime pillow numpy

备注:
- 默认输入分辨率 518×518（DINOv3 官方推理规格），dynamic_shapes 只放开 batch 维度。
- 输出两个张量：
    - cls_embedding : [B, D]          — last_hidden_state[:, 0, :]
    - patch_tokens  : [B, N*N, D]     — 自动取末尾 N*N 个 token，跳过 CLS 与 register
  DINOv3 带 register token（通常 4 个），顺序为 [CLS, register..., patch...]，
  因此用 h[:, -num_patch_tokens:, :] 切片最稳。
- DINOv3 权重受 Meta DINOv3 License 约束，打包分发前需 review。
"""

from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

import torch
from transformers import AutoModel


class DinoFeatureWrapper(torch.nn.Module):
    """双输出包装：CLS + patch grid；patch 自动跳过 register token。"""

    def __init__(self, backbone: torch.nn.Module, num_patch_tokens: int) -> None:
        super().__init__()
        self.backbone = backbone
        self.num_patch_tokens = num_patch_tokens

    def forward(self, pixel_values: torch.Tensor):
        h = self.backbone(pixel_values=pixel_values).last_hidden_state
        # h: [B, 1 + R + N*N, D]，R 为 register 数（DINOv3 通常为 4）
        cls = h[:, 0, :]
        patch = h[:, -self.num_patch_tokens:, :]
        return cls, patch


def infer_patch_grid(backbone: torch.nn.Module, image_size: int) -> int:
    """根据 backbone.config.patch_size 推断方形 patch 网格边长。"""
    patch_size = getattr(backbone.config, "patch_size", None)
    if patch_size is None:
        raise RuntimeError("backbone.config 没有 patch_size 字段，无法推断 patch 数量")
    if image_size % patch_size != 0:
        print(
            f"[export] warn: image_size={image_size} 不能被 patch_size={patch_size} 整除，"
            f"按下取整处理（grid={image_size // patch_size}）",
            flush=True,
        )
    return image_size // patch_size


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Export DINOv3 ViT backbone (CLS + patch) to ONNX")
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

    grid = infer_patch_grid(model, args.image_size)
    num_patch_tokens = grid * grid
    feature_dim = int(getattr(model.config, "hidden_size", 0))
    print(
        f"[export] patch grid = {grid}x{grid} ({num_patch_tokens} tokens)，"
        f"hidden_size = {feature_dim}",
        flush=True,
    )

    if args.fp16:
        model = model.half()
        dtype = torch.float16
    else:
        dtype = torch.float32

    wrapper = DinoFeatureWrapper(model, num_patch_tokens).eval()
    dummy = torch.randn(1, 3, args.image_size, args.image_size, dtype=dtype)

    # 导出前跑一次前向，顺便校验总 token 数与切片假设一致。
    with torch.no_grad():
        cls_ref, patch_ref = wrapper(dummy)
    print(
        f"[export] sanity: cls={tuple(cls_ref.shape)}  patch={tuple(patch_ref.shape)}",
        flush=True,
    )
    if patch_ref.shape[1] != num_patch_tokens:
        raise RuntimeError(
            f"patch token 数量异常：期望 {num_patch_tokens}，实际 {patch_ref.shape[1]}"
        )

    print(f"[export] exporting to {out_path} (opset={args.opset}, dtype={dtype})", flush=True)
    # external_data=False：把所有权重内嵌到单个 .onnx 文件，Avalonia 作为资源加载只需一份文件。
    # dynamic_shapes 用于 torch 2.x dynamo 导出器放开 batch 维。
    with torch.no_grad():
        torch.onnx.export(
            wrapper,
            dummy,
            str(out_path),
            input_names=["pixel_values"],
            output_names=["cls_embedding", "patch_tokens"],
            opset_version=args.opset,
            do_constant_folding=True,
            external_data=False,
        )

    size_mb = os.path.getsize(out_path) / (1024 * 1024)
    print(f"[export] done: {out_path} ({size_mb:.1f} MB)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
