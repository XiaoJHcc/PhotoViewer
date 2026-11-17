using System;
using System.Text;
using DirectN;

namespace PhotoViewer.Windows.Native;

internal sealed partial class WindowsPhotoView
{
    private const string OutputColorShader = """
        Texture2D<float4> scene : register(t0);
        Texture3D<float4> colorLut : register(t1);
        SamplerState colorSampler : register(s0);
        #define MaxBackdropRegions 32
        cbuffer OutputColor : register(b0)
        {
            float4 row0; float4 row1; float4 row2; float4 cube; float4 backdropCount;
            float4 backdropRect[MaxBackdropRegions];    // xy = 中心(物理像素), zw = 半宽高
            float4 backdropRadius[MaxBackdropRegions];  // x = 圆角半径(物理像素)
        };
        struct OutputVertex { float4 position : SV_Position; float2 uv : TEXCOORD0; };
        OutputVertex ColorVS(uint id : SV_VertexID)
        {
            OutputVertex o;
            o.uv = float2(id & 1, id >> 1);
            o.position = float4(o.uv.x * 2 - 1, 1 - o.uv.y * 2, 0, 1);
            return o;
        }
        float3 DecodeDevice(float3 v)
        {
            return float3(v.r <= 0.04045 ? v.r / 12.92 : pow(max(0, (v.r + 0.055) / 1.055), 2.4),
                          v.g <= 0.04045 ? v.g / 12.92 : pow(max(0, (v.g + 0.055) / 1.055), 2.4),
                          v.b <= 0.04045 ? v.b / 12.92 : pow(max(0, (v.b + 0.055) / 1.055), 2.4));
        }
        // 交换链内的亚克力区段已是与 XAML 亚克力同一份编码,不再走显示器转换;圆角按与合成阶段同一套 SDF 判定。
        bool BackdropCovers(float2 pixel)
        {
            [loop] for (int i = 0; i < (int)backdropCount.x; i++)
            {
                float2 extent = backdropRect[i].zw;
                float radius = clamp(backdropRadius[i].x, 0.0, min(extent.x, extent.y));
                float2 distance = abs(pixel - backdropRect[i].xy) - (extent - radius);
                float outside = length(max(distance, 0.0)) + min(max(distance.x, distance.y), 0.0) - radius;
                if (outside < 0.0) return true;
            }
            return false;
        }
        float4 ColorPS(OutputVertex i) : SV_Target
        {
            float4 c = scene.SampleLevel(colorSampler, i.uv, 0);
            if (cube.w == 0 || BackdropCovers(i.position.xy)) return float4(c.rgb * cube.z, c.a);
            float3 wide = float3(dot(row0.xyz, c.rgb), dot(row1.xyz, c.rgb), dot(row2.xyz, c.rgb));
            float3 encoded = pow(saturate(wide), 1.0 / 1.80078125);
            float3 device = colorLut.SampleLevel(colorSampler, encoded * cube.x + cube.y, 0).rgb;
            // Legacy DWM re-encodes scRGB; the backing acrylic remains in the same space as XAML.
            return float4(DecodeDevice(device), c.a);
        }
        """;

    private IComObject<ID3D11VertexShader>? _outputVertex;
    private IComObject<ID3D11PixelShader>? _outputPixel;
    private IComObject<ID3D11Buffer>? _outputParameters;
    private IComObject<ID3D11Texture3D>? _outputLut;
    private IComObject<ID3D11ShaderResourceView>? _outputLutView;
    private IComObject<ID3D11Texture2D>? _outputScene;
    private IComObject<ID3D11ShaderResourceView>? _outputSceneView;
    private WindowsDisplayTransform? _outputTransform;
    /// <summary>输出转换里最多能排除多少个亚克力区段(与着色器 <c>MaxBackdropRegions</c> 一致)。</summary>
    private const int OutputBackdropCapacity = 32;

    /// <summary>
    /// 输出转换常量:0–15 为矩阵与立方图参数,16 起为交换链内的亚克力区段(16 数量、20 起矩形、其后为圆角半径)。
    /// </summary>
    private readonly float[] _outputParameterData = new float[20 + (OutputBackdropCapacity * 8)];
    private (uint Width, uint Height) _outputSceneSize;
    private long _outputColorBytes;

    internal void UpdateDisplayColor()
    {
        if (_disposed) return;
        ApplyOutputTransform(WindowsDisplayColor.Current);
        Draw();
    }

    internal void SetDisplayTransformForChecks(WindowsDisplayTransform transform)
    {
        ApplyOutputTransform(transform);
        Draw();
    }

    private unsafe void ApplyOutputTransform(WindowsDisplayTransform transform)
    {
        if (ReferenceEquals(_outputTransform, transform)) return;
        IComObject<ID3D11Texture3D>? nextTexture = null;
        IComObject<ID3D11ShaderResourceView>? nextView = null;
        try
        {
            if (transform.Lut != null || transform.SdrWhiteScale != 1) EnsureOutputShaders();
            if (transform.Lut is { } lut)
            {
                var half = new Half[lut.Length];
                for (int i = 0; i < lut.Length; i++) half[i] = (Half)lut[i];
                var desc = new D3D11_TEXTURE3D_DESC
                {
                    Width = (uint)transform.Size, Height = (uint)transform.Size, Depth = (uint)transform.Size,
                    MipLevels = 1, Format = DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT,
                    Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE,
                    BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                };
                fixed (Half* pointer = half)
                {
                    _device.Object.CreateTexture3D(ref desc, [new D3D11_SUBRESOURCE_DATA
                    {
                        pSysMem = (IntPtr)pointer, SysMemPitch = (uint)(transform.Size * 8),
                        SysMemSlicePitch = (uint)(transform.Size * transform.Size * 8),
                    }], out var texture).ThrowOnError();
                    nextTexture = new ComObject<ID3D11Texture3D>(texture, true);
                }
                nextView = _device.Object.CreateShaderResourceView(nextTexture.Object, null);
            }
            _context.Object.PSSetShaderResources(0, 2, [null!, null!]);
            _outputLutView?.Dispose();
            _outputLut?.Dispose();
            _outputLut = nextTexture;
            _outputLutView = nextView;
            nextTexture = null;
            nextView = null;
            _outputTransform = transform;
            if (transform.Lut == null && transform.SdrWhiteScale == 1) ReleaseOutputScene();
            UpdateOutputColorBytes();
        }
        finally { nextView?.Dispose(); nextTexture?.Dispose(); }
    }

    private void EnsureOutputShaders()
    {
        if (_outputVertex != null && _outputPixel != null && _outputParameters != null) return;
        _outputVertex?.Dispose();
        _outputPixel?.Dispose();
        _outputParameters?.Dispose();
        _outputVertex = null;
        _outputPixel = null;
        _outputParameters = null;
        var source = Encoding.UTF8.GetBytes(OutputColorShader);
        _outputVertex = _device.CreateVertexShader(D3D11Functions.D3DCompile(source, "display-color.hlsl", "ColorVS", "vs_5_0", 0));
        _outputPixel = _device.CreatePixelShader(D3D11Functions.D3DCompile(source, "display-color.hlsl", "ColorPS", "ps_5_0", 0));
        _outputParameters = _device.CreateBuffer(new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)(_outputParameterData.Length * sizeof(float)), Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
        }, null);
    }

    private unsafe void RenderOutputColor()
    {
        if (_renderTarget == null || _outputTransform == null
            || (_outputLutView == null && _outputTransform.SdrWhiteScale == 1)) return;
        if (_outputSceneSize != (_pixelWidth, _pixelHeight))
        {
            ReleaseOutputScene();
            _outputScene = _device.CreateTexture2D(new D3D11_TEXTURE2D_DESC
            {
                Width = _pixelWidth, Height = _pixelHeight, MipLevels = 1, ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            }, null);
            _outputSceneView = _device.CreateShaderResourceView(_outputScene, null);
            _outputSceneSize = (_pixelWidth, _pixelHeight);
            UpdateOutputColorBytes();
        }
        var m = WindowsDisplayTransform.LinearSrgbToWide;
        for (int row = 0; row < 3; row++) for (int col = 0; col < 3; col++) _outputParameterData[row * 4 + col] = m[row * 3 + col];
        int size = Math.Max(1, _outputTransform.Size);
        _outputParameterData[12] = (size - 1f) / size;
        _outputParameterData[13] = 0.5f / size;
        _outputParameterData[14] = _outputTransform.SdrWhiteScale;
        _outputParameterData[15] = _outputLutView == null ? 0 : 1;
        Array.Clear(_outputParameterData, 16, _outputParameterData.Length - 16);
        if (!_backdropFailed && _backdropRegions.Length > 0)
        {
            // 区段矩形取与合成阶段同一套整像素边界:中心与半宽高都在像素中心坐标系里。
            double scale = PhysicalPixelsPerDip;
            const int RectOffset = 20;
            int radiusOffset = RectOffset + (OutputBackdropCapacity * 4);
            int count = Math.Min(_backdropRegions.Length, OutputBackdropCapacity);
            _outputParameterData[16] = count;
            for (var index = 0; index < count; index++)
            {
                var region = _backdropRegions[index];
                double left = Math.Round(region.Bounds.X * scale);
                double top = Math.Round(region.Bounds.Y * scale);
                double right = Math.Round((region.Bounds.X + region.Bounds.Width) * scale);
                double bottom = Math.Round((region.Bounds.Y + region.Bounds.Height) * scale);
                int offset = RectOffset + (index * 4);
                _outputParameterData[offset] = (float)((left + right) / 2);
                _outputParameterData[offset + 1] = (float)((top + bottom) / 2);
                _outputParameterData[offset + 2] = (float)((right - left) / 2);
                _outputParameterData[offset + 3] = (float)((bottom - top) / 2);
                _outputParameterData[radiusOffset + (index * 4)] = (float)Math.Clamp(
                    region.CornerRadius * scale, 0d, Math.Min(right - left, bottom - top) / 2);
            }
        }
        fixed (float* pointer = _outputParameterData)
            _context.Object.UpdateSubresource(_outputParameters!.Object, 0, IntPtr.Zero, (IntPtr)pointer, 0, 0);
        _context.Object.OMSetRenderTargets(0, null, null);
        _context.Object.PSSetShaderResources(0, 2, [null!, null!]);
        using (var buffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
            _context.Object.CopyResource(_outputScene!.Object, buffer.Object);
        _context.Object.OMSetRenderTargets(1, [_renderTarget.Object], null);
        _context.Object.RSSetViewports(1, [new D3D11_VIEWPORT { Width = _pixelWidth, Height = _pixelHeight, MinDepth = 0, MaxDepth = 1 }]);
        _context.Object.IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        _context.Object.VSSetShader(_outputVertex!.Object, null, 0);
        _context.Object.PSSetShader(_outputPixel!.Object, null, 0);
        _context.Object.PSSetConstantBuffers(0, 1, [_outputParameters!.Object]);
        _context.Object.PSSetShaderResources(0, 2, [_outputSceneView!.Object, _outputLutView?.Object!]);
        _context.Object.PSSetSamplers(0, 1, [_sampler.Object]);
        _context.Object.Draw(4, 0);
        _context.Object.PSSetShaderResources(0, 2, [null!, null!]);
    }

    private void ReleaseOutputScene()
    {
        _outputSceneView?.Dispose();
        _outputSceneView = null;
        _outputScene?.Dispose();
        _outputScene = null;
        _outputSceneSize = default;
    }

    private void UpdateOutputColorBytes()
    {
        _outputColorBytes = (_outputTransform?.Lut?.LongLength ?? 0) * sizeof(ushort)
            + (long)_outputSceneSize.Width * _outputSceneSize.Height * 8;
    }

    private void DisposeOutputColor()
    {
        ReleaseOutputScene();
        _outputLutView?.Dispose();
        _outputLut?.Dispose();
        _outputParameters?.Dispose();
        _outputPixel?.Dispose();
        _outputVertex?.Dispose();
        _outputColorBytes = 0;
    }
}
