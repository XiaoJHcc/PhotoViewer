#!/usr/bin/env python3
"""
Training/onnx/export_dinov3_onnx.py

将 HuggingFace 上的 DINOv3 ViT 导出为 ONNX，双输出（CLS + patch tokens）。
产物用于 PhotoViewer 端侧特征提取（A2-M1）。

用法:
    python Training/onnx/export_dinov3_onnx.py \
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

import numpy as np
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

    # DirectML 不支持 Reshape 中的 -1（推断维度），需要后处理把 -1 替换为实际值。
    _fix_reshape_for_directml(out_path)

    size_mb = os.path.getsize(out_path) / (1024 * 1024)
    print(f"[export] done: {out_path} ({size_mb:.1f} MB)")
    return 0


def _fix_reshape_for_directml(path: Path) -> None:
    """用 ONNX shape inference 解析所有 Reshape 节点中的 -1 维度，替换为静态值。"""
    import onnx
    from onnx import shape_inference

    model = onnx.load(str(path))
    model = shape_inference.infer_shapes(model)

    shape_map: dict[str, list[int]] = {}
    for vi in model.graph.value_info:
        t = vi.type.tensor_type
        if t.HasField("shape"):
            dims = [d.dim_value if d.HasField("dim_value") else -1 for d in t.shape.dim]
            shape_map[vi.name] = dims

    init_map = {i.name: i for i in model.graph.initializer}
    fixed = 0
    for node in model.graph.node:
        if node.op_type != "Reshape":
            continue
        shape_name = node.input[1]
        if shape_name not in init_map:
            continue
        init = init_map[shape_name]
        shape_val = np.frombuffer(init.raw_data, dtype=np.int64).copy()
        if -1 not in shape_val:
            continue
        out_name = node.output[0]
        if out_name in shape_map:
            actual = shape_map[out_name]
            if -1 not in actual and len(actual) == len(shape_val):
                init.raw_data = np.array(actual, dtype=np.int64).tobytes()
                fixed += 1

    if fixed > 0:
        onnx.save(model, str(path))
        print(f"[export] fixed {fixed} Reshape nodes with -1 dims (DirectML compat)", flush=True)


if __name__ == "__main__":
    sys.exit(main())
