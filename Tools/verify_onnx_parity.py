#!/usr/bin/env python3
"""
Tools/verify_onnx_parity.py

校验导出的 DINOv3 ONNX 与 PyTorch 原始模型在相同输入下的特征向量一致性。
A2-M1 验收门：随机 100 张样本的 cosine 相似度均值 ≥ 0.999。

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
    a = a / (np.linalg.norm(a, axis=-1, keepdims=True) + 1e-12)
    b = b / (np.linalg.norm(b, axis=-1, keepdims=True) + 1e-12)
    return (a * b).sum(axis=-1)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Verify ONNX export parity against PyTorch")
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

    print(f"[verify] loading onnx session {args.onnx}", flush=True)
    sess = ort.InferenceSession(args.onnx, providers=["CPUExecutionProvider"])

    cosines = []
    max_abs = 0.0
    for i in range(args.samples):
        x = rng.standard_normal((1, 3, args.image_size, args.image_size)).astype(np.float32)

        with torch.no_grad():
            torch_out = torch_model(pixel_values=torch.from_numpy(x)).last_hidden_state[:, 0, :].numpy()

        onnx_out = sess.run(["cls_embedding"], {"pixel_values": x})[0]

        cos = float(cosine(torch_out, onnx_out).mean())
        diff = float(np.abs(torch_out - onnx_out).max())
        cosines.append(cos)
        max_abs = max(max_abs, diff)

        if (i + 1) % 10 == 0:
            print(f"[verify] {i + 1}/{args.samples}  cos_mean={np.mean(cosines):.6f}  max_abs={max_abs:.4e}", flush=True)

    mean_cos = float(np.mean(cosines))
    min_cos = float(np.min(cosines))
    print(f"[verify] done: cos_mean={mean_cos:.6f}  cos_min={min_cos:.6f}  max_abs={max_abs:.4e}")

    if min_cos < args.threshold:
        print(f"[verify] FAIL: min cosine {min_cos:.6f} < threshold {args.threshold}", file=sys.stderr)
        return 1
    print(f"[verify] PASS: min cosine {min_cos:.6f} >= threshold {args.threshold}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
