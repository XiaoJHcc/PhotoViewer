namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 上游 AGSL 着色器的逐字副本（<c>com.kyant.backdrop.internal/Shaders.kt</c> 与示例
/// <c>utils/InteractiveHighlight.kt</c>）。常量、函数与语句顺序与上游一致，不在这里改写算法。
/// </summary>
/// <remarks>
/// 共享的圆角矩形有向距离场单独保存，折射与高光着色器按上游的 <c>$RoundedRectSDF</c>
/// 插值位置拼接。运行时字符串见 <see cref="RoundedRectRefraction"/> 等常量。
/// </remarks>
internal static class BackdropShaders
{
    /// <summary>圆角矩形 SDF 与梯度，上游 <c>RoundedRectSDF</c>。</summary>
    private const string RoundedRectSdf = """

        float radiusAt(float2 centeredCoord, float4 radii) {
            if (centeredCoord.x >= 0.0) {
                if (centeredCoord.y <= 0.0) return radii.y;
                else return radii.z;
            } else {
                if (centeredCoord.y <= 0.0) return radii.x;
                else return radii.w;
            }
        }

        float sdRoundedRect(float2 coord, float2 halfSize, float radius) {
            float2 cornerCoord = abs(coord) - (halfSize - float2(radius));
            float outside = length(max(cornerCoord, 0.0)) - radius;
            float inside = min(max(cornerCoord.x, cornerCoord.y), 0.0);
            return outside + inside;
        }

        float2 gradSdRoundedRect(float2 coord, float2 halfSize, float radius) {
            float2 cornerCoord = abs(coord) - (halfSize - float2(radius));
            if (cornerCoord.x >= 0.0 || cornerCoord.y >= 0.0) {
                return sign(coord) * normalize(max(cornerCoord, 0.0));
            } else {
                float gradX = step(cornerCoord.y, cornerCoord.x);
                return sign(coord) * float2(gradX, 1.0 - gradX);
            }
        }
        """;

    /// <summary>透镜折射，上游 <c>RoundedRectRefractionShaderString</c>。</summary>
    internal const string RoundedRectRefraction = """
        uniform shader content;

        uniform float2 size;
        uniform float2 offset;
        uniform float4 cornerRadii;
        uniform float refractionHeight;
        uniform float refractionAmount;
        uniform float depthEffect;


        """ + RoundedRectSdf + """


        float circleMap(float x) {
            return 1.0 - sqrt(1.0 - x * x);
        }

        half4 main(float2 coord) {
            float2 halfSize = size * 0.5;
            float2 centeredCoord = (coord + offset) - halfSize;
            float radius = radiusAt(centeredCoord, cornerRadii);

            float sd = sdRoundedRect(centeredCoord, halfSize, radius);
            if (-sd >= refractionHeight) {
                return content.eval(coord);
            }
            sd = min(sd, 0.0);

            float d = circleMap(1.0 - -sd / refractionHeight) * refractionAmount;
            float gradRadius = min(radius * 1.5, min(halfSize.x, halfSize.y));
            float2 grad = normalize(gradSdRoundedRect(centeredCoord, halfSize, gradRadius) + depthEffect * normalize(centeredCoord));

            float2 refractedCoord = coord + d * grad;
            return content.eval(refractedCoord);
        }
        """;

    /// <summary>带色散的透镜折射，上游 <c>RoundedRectRefractionWithDispersionShaderString</c>。</summary>
    internal const string RoundedRectRefractionWithDispersion = """
        uniform shader content;

        uniform float2 size;
        uniform float2 offset;
        uniform float4 cornerRadii;
        uniform float refractionHeight;
        uniform float refractionAmount;
        uniform float depthEffect;
        uniform float chromaticAberration;


        """ + RoundedRectSdf + """


        float circleMap(float x) {
            return 1.0 - sqrt(1.0 - x * x);
        }

        half4 main(float2 coord) {
            float2 halfSize = size * 0.5;
            float2 centeredCoord = (coord + offset) - halfSize;
            float radius = radiusAt(centeredCoord, cornerRadii);

            float sd = sdRoundedRect(centeredCoord, halfSize, radius);
            if (-sd >= refractionHeight) {
                return content.eval(coord);
            }
            sd = min(sd, 0.0);

            float d = circleMap(1.0 - -sd / refractionHeight) * refractionAmount;
            float gradRadius = min(radius * 1.5, min(halfSize.x, halfSize.y));
            float2 grad = normalize(gradSdRoundedRect(centeredCoord, halfSize, gradRadius) + depthEffect * normalize(centeredCoord));

            float2 refractedCoord = coord + d * grad;
            float dispersionIntensity = chromaticAberration * ((centeredCoord.x * centeredCoord.y) / (halfSize.x * halfSize.y));
            float2 dispersedCoord = d * grad * dispersionIntensity;

            half4 color = half4(0.0);

            half4 red = content.eval(refractedCoord + dispersedCoord);
            color.r += red.r / 3.5;
            color.a += red.a / 7.0;

            half4 orange = content.eval(refractedCoord + dispersedCoord * (2.0 / 3.0));
            color.r += orange.r / 3.5;
            color.g += orange.g / 7.0;
            color.a += orange.a / 7.0;

            half4 yellow = content.eval(refractedCoord + dispersedCoord * (1.0 / 3.0));
            color.r += yellow.r / 3.5;
            color.g += yellow.g / 3.5;
            color.a += yellow.a / 7.0;

            half4 green = content.eval(refractedCoord);
            color.g += green.g / 3.5;
            color.a += green.a / 7.0;

            half4 cyan = content.eval(refractedCoord - dispersedCoord * (1.0 / 3.0));
            color.g += cyan.g / 3.5;
            color.b += cyan.b / 3.0;
            color.a += cyan.a / 7.0;

            half4 blue = content.eval(refractedCoord - dispersedCoord * (2.0 / 3.0));
            color.b += blue.b / 3.0;
            color.a += blue.a / 7.0;

            half4 purple = content.eval(refractedCoord - dispersedCoord);
            color.r += purple.r / 7.0;
            color.b += purple.b / 3.0;
            color.a += purple.a / 7.0;

            return color;
        }
        """;

    /// <summary>默认高光，上游 <c>DefaultHighlightShaderString</c>。</summary>
    internal const string DefaultHighlight = """
        uniform float2 size;
        uniform float4 cornerRadii;
        layout(color) uniform half4 color;
        uniform float angle;
        uniform float falloff;


        """ + RoundedRectSdf + """


        half4 main(float2 coord) {
            float2 halfSize = size * 0.5;
            float2 centeredCoord = coord - halfSize;
            float radius = radiusAt(centeredCoord, cornerRadii);

            float gradRadius = min(radius * 1.5, min(halfSize.x, halfSize.y));
            float2 grad = gradSdRoundedRect(centeredCoord, halfSize, gradRadius);
            float2 normal = float2(cos(angle), sin(angle));
            float d = dot(grad, normal);
            float intensity = pow(abs(d), falloff);
            return color * intensity;
        }
        """;

    /// <summary>环境高光，上游 <c>AmbientHighlightShaderString</c>。</summary>
    internal const string AmbientHighlight = """
        uniform float2 size;
        uniform float4 cornerRadii;
        uniform float angle;
        uniform float falloff;


        """ + RoundedRectSdf + """


        half4 main(float2 coord) {
            float2 halfSize = size * 0.5;
            float2 centeredCoord = coord - halfSize;
            float radius = radiusAt(centeredCoord, cornerRadii);

            float gradRadius = min(radius * 1.5, min(halfSize.x, halfSize.y));
            float2 grad = gradSdRoundedRect(centeredCoord, halfSize, gradRadius);
            float2 normal = float2(cos(angle), sin(angle));
            float d = dot(grad, normal);
            float intensity = pow(abs(d), falloff);
            float t = step(0.0, d);
            return half4(t, t, t, 1.0) * intensity;
        }
        """;

    /// <summary>按压跟手径向高光，上游示例 <c>InteractiveHighlight.kt</c> 的内联着色器。</summary>
    internal const string InteractiveHighlight = """
        uniform float2 size;
        layout(color) uniform half4 color;
        uniform float radius;
        uniform float2 position;

        half4 main(float2 coord) {
            float dist = distance(coord, position);
            float intensity = smoothstep(radius, radius * 0.5, dist);
            return color * intensity;
        }
        """;
}
