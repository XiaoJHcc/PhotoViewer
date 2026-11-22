using System;
using System.Runtime.Versioning;
using Android.Graphics;

namespace PhotoViewer.Android.Native;

internal static class AndroidGammaToneMapping
{
    private const string ShaderSource = """
        uniform shader content;
        uniform float gamma;

        half4 main(float2 xy) {
            half4 color = content.eval(xy);
            color.rgb = pow(max(color.rgb, half3(0.0)), half3(gamma));
            return color;
        }
        """;

    private static readonly object _gate = new();
    private static float _cachedGamma;
    private static RenderEffect? _cachedEffect;

    [SupportedOSPlatform("android33.0")]
    internal static RenderEffect CreateEffect(float gamma)
    {
        if (gamma <= 0f) throw new ArgumentOutOfRangeException(nameof(gamma));
        lock (_gate)
        {
            if (_cachedEffect != null && Math.Abs(_cachedGamma - gamma) < 1e-6) return _cachedEffect;
            var shader = new RuntimeShader(ShaderSource);
            shader.SetFloatUniform("gamma", gamma);
            _cachedEffect = RenderEffect.CreateRuntimeShaderEffect(shader, "content")!;
            _cachedGamma = gamma;
            return _cachedEffect;
        }
    }
}
