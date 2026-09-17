Texture2D<float4> Plane0 : register(t0);
Texture2D<float4> Plane1 : register(t1);
Texture2D<float4> Plane2 : register(t2);
Texture2D<float4> Plane3 : register(t3);
SamplerState LinearSampler : register(s0);

cbuffer Conversion : register(b0)
{
    uint InputMode;
    uint TransferMode;
    uint LinearOutput;
    uint HasAlpha;

    float4 PlaneScale;
    float4 YuvRange;
    float4 MatrixR;
    float4 MatrixG;
    float4 MatrixB;
    float4 ChromaOffset;
};

struct VertexOutput
{
    float4 Position : SV_Position;
    float2 TexCoord : TEXCOORD0;
};

VertexOutput VSMain(uint vertexId : SV_VertexID)
{
    VertexOutput output;
    output.TexCoord = float2((vertexId << 1) & 2, vertexId & 2);
    output.Position = float4(output.TexCoord * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return output;
}

float3 ToLinear(float3 value)
{
    value = max(value, 0.0);
    if (TransferMode == 1)
        return value;
    if (TransferMode == 2)
        return lerp(value / 12.92, pow((value + 0.055) / 1.055, 2.4), step(0.04045, value));
    if (TransferMode == 3)
        return pow(value, 2.2);
    if (TransferMode == 4)
        return pow(value, 2.8);
    return lerp(value / 4.5, pow((value + 0.099) / 1.099, 1.0 / 0.45), step(0.081, value));
}

float4 PSMain(VertexOutput input) : SV_Target
{
    float2 uv = input.TexCoord;
    float4 color;

    if (InputMode == 0 || InputMode == 1)
    {
        float y = Plane0.SampleLevel(LinearSampler, uv, 0).r * PlaneScale.x;
        float2 chromaUv = uv + ChromaOffset.xy;
        float u;
        float v;
        if (InputMode == 1)
        {
            float2 packedUv = Plane1.SampleLevel(LinearSampler, chromaUv, 0).rg;
            u = packedUv.x * PlaneScale.y;
            v = packedUv.y * PlaneScale.z;
        }
        else
        {
            u = Plane1.SampleLevel(LinearSampler, chromaUv, 0).r * PlaneScale.y;
            v = Plane2.SampleLevel(LinearSampler, chromaUv, 0).r * PlaneScale.z;
        }

        float normalizedY = (y - YuvRange.x) * YuvRange.y;
        float normalizedU = (u - YuvRange.z) * YuvRange.w;
        float normalizedV = (v - YuvRange.z) * YuvRange.w;
        float3 yuv = float3(normalizedY, normalizedU, normalizedV);
        color.rgb = float3(dot(MatrixR.xyz, yuv), dot(MatrixG.xyz, yuv), dot(MatrixB.xyz, yuv));
        color.a = HasAlpha != 0 ? Plane3.SampleLevel(LinearSampler, uv, 0).r * PlaneScale.w : 1.0;
    }
    else if (InputMode == 2)
    {
        float r = Plane0.SampleLevel(LinearSampler, uv, 0).r * PlaneScale.x;
        float g = Plane1.SampleLevel(LinearSampler, uv, 0).r * PlaneScale.y;
        float b = Plane2.SampleLevel(LinearSampler, uv, 0).r * PlaneScale.z;
        color = float4(r, g, b, HasAlpha != 0 ? Plane3.SampleLevel(LinearSampler, uv, 0).r * PlaneScale.w : 1.0);
    }
    else if (InputMode == 4)
    {
        float4 packed = Plane0.SampleLevel(LinearSampler, uv, 0);
        color = float4(packed.b, packed.g, packed.r, HasAlpha != 0 ? packed.a : 1.0);
    }
    else
    {
        color = Plane0.SampleLevel(LinearSampler, uv, 0);
        if (HasAlpha == 0)
            color.a = 1.0;
    }

    if (LinearOutput != 0)
        color.rgb = ToLinear(color.rgb);
    return saturate(color);
}
