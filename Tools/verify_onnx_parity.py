#!/usr/bin/env python3
"""
Tools/verify_onnx_parity.py

校验导出的 DINOv3 ONNX 与 PyTorch 原始模型在相同输入下的特征一致性。
A2-M1 验收门：
- CLS cosine 均值 ≥ threshold（默认 0.999）
- patch token 逐位置 cosine 均值 ≥ threshold（默认 0.999）
随机 100 张样本。

用法:
    python Tools/verify_onnx_parity.py \
        --model-id facebook/dinov3-vits16-pretrain-lvd1689m \
        --onnx PhotoViewer/Assets/Models/dinov3_vits16.onnx \
        --samples 100

依赖:
    pip install torch transformers onnxruntime numpy
"""

from __future__ import annotations

import argparse
import sys

import numpy as np
import onnxruntime as ort
import torch
from transformers import AutoModel


def cosine(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    """沿最后一维做 cosine；支持任意前置维度。"""
    a = a / (np.linalg.norm(a, axis=-1, keepdims=True) + 1e-12)
    b = b / (np.linalg.norm(b, axis=-1, keepdims=True) + 1e-12)
    return (a * b).sum(axis=-1)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Verify ONNX export parity against PyTorch (CLS + patch)")
    parser.add_argument("--model-id", default="facebook/dinov3-vits16-pretrain-lvd1689m")
    parser.add_argument("--onnx", default="PhotoViewer/Assets/Models/dinov3_vits16.onnx")
    parser.add_argument("--samples", type=int, default=100)
    parser.add_argument("--image-size", type=int, default=518)
    parser.add_argument("--threshold", type=float, default=0.999)
    parser.add_argument("--seed", type=int, default=0)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    rng = np.random.default_rng(args.seed)

    print(f"[verify] loading torch model {args.model_id}", flush=True)
    torch_model = AutoModel.from_pretrained(args.model_id).eval()

    patch_size = getattr(torch_model.config, "patch_size", None)
    if patch_size is None:
        print("[verify] FAIL: torch model config 缺 patch_size", file=sys.stderr)
        return 2
    grid = args.image_size // patch_size
    num_patch_tokens = grid * grid

    print(f"[verify] loading onnx session {args.onnx}", flush=True)
    sess = ort.InferenceSession(args.onnx, providers=["CPUExecutionProvider"])

    onnx_outputs = {o.name for o in sess.get_outputs()}
    expected = {"cls_embedding", "patch_tokens"}
    missing = expected - onnx_outputs
    if missing:
        print(f"[verify] FAIL: ONNX 缺输出 {missing}；实际 {onnx_outputs}", file=sys.stderr)
        return 2

    cls_cosines: list[float] = []
    patch_cosines: list[float] = []
    cls_max_abs = 0.0
    patch_max_abs = 0.0

    for i in range(args.samples):
        x = rng.standard_normal((1, 3, args.image_size, args.image_size)).astype(np.float32)

        with torch.no_grad():
            h = torch_model(pixel_values=torch.from_numpy(x)).last_hidden_state.numpy()
        torch_cls = h[:, 0, :]
        torch_patch = h[:, -num_patch_tokens:, :]

        onnx_cls, onnx_patch = sess.run(["cls_embedding", "patch_tokens"], {"pixel_values": x})

        if onnx_patch.shape[1] != num_patch_tokens:
            print(
                f"[verify] FAIL: patch token 数量不一致，ONNX={onnx_patch.shape[1]} "
                f"期望={num_patch_tokens}",
                file=sys.stderr,
            )
            return 2

        cls_cos = float(cosine(torch_cls, onnx_cls).mean())
        patch_cos = float(cosine(torch_patch, onnx_patch).mean())  # 逐 token 后再求均值

        cls_cosines.append(cls_cos)
        patch_cosines.append(patch_cos)
        cls_max_abs = max(cls_max_abs, float(np.abs(torch_cls - onnx_cls).max()))
        patch_max_abs = max(patch_max_abs, float(np.abs(torch_patch - onnx_patch).max()))

        if (i + 1) % 10 == 0:
            print(
                f"[verify] {i + 1}/{args.samples}  "
                f"cls_mean={np.mean(cls_cosines):.6f}  patch_mean={np.mean(patch_cosines):.6f}  "
                f"cls_max_abs={cls_max_abs:.4e}  patch_max_abs={patch_max_abs:.4e}",
                flush=True,
            )

    cls_mean = float(np.mean(cls_cosines))
    cls_min = float(np.min(cls_cosines))
    patch_mean = float(np.mean(patch_cosines))
    patch_min = float(np.min(patch_cosines))
    print(
        f"[verify] done: cls(mean={cls_mean:.6f}, min={cls_min:.6f}, max_abs={cls_max_abs:.4e})  "
        f"patch(mean={patch_mean:.6f}, min={patch_min:.6f}, max_abs={patch_max_abs:.4e})"
    )

    thr = args.threshold
    fail = []
    if cls_min < thr:
        fail.append(f"cls min {cls_min:.6f} < {thr}")
    if patch_min < thr:
        fail.append(f"patch min {patch_min:.6f} < {thr}")
    if fail:
        print("[verify] FAIL: " + "; ".join(fail), file=sys.stderr)
        return 1
    print(f"[verify] PASS: cls_min={cls_min:.6f}  patch_min={patch_min:.6f}  >= {thr}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
