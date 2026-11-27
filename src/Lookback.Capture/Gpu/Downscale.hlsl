// Analysis downscale: any captured texture to the fixed analysis format
// (384x216 BGRA8 by default), letterboxed exactly like FileSource's
// scale=force_original_aspect_ratio=decrease + centred black pad. Each output
// pixel averages a grid of bilinear taps over its footprint, which
// approximates an area filter. Output is stored into an R8G8B8A8 UAV with
// swizzled channels so the bytes read back as B,G,R,A (typed UAV stores to
// B8G8R8A8 are not guaranteed on feature level 11.0).

Texture2D<float4> Src : register(t0);
RWTexture2D<unorm float4> Dst : register(u0);
SamplerState Linear : register(s0);

cbuffer Params : register(b0)
{
    float2 SrcSize;     // source pixels
    float2 DstSize;     // output pixels
    float2 Offset;      // top-left of the content rectangle in output pixels
    float2 Content;     // size of the content rectangle in output pixels
    uint   Transfer;    // 0 = SDR as stored, 1 = scRGB linear (FP16), 2 = HDR10 PQ
    float  SdrWhite;    // nits that map to SDR white for HDR input
    float2 Pad;
};

float3 Pq(float3 e)
{
    const float m1 = 0.1593017578125, m2 = 78.84375, c1 = 0.8359375, c2 = 18.8515625, c3 = 18.6875;
    float3 p = pow(saturate(e), 1.0 / m2);
    return pow(max(p - c1, 0) / (c2 - c3 * p), 1.0 / m1) * 10000.0; // nits
}

float3 ToSdr(float3 c)
{
    if (Transfer == 0) return c;
    float3 nits;
    if (Transfer == 1)
    {
        nits = c * 80.0;
    }
    else
    {
        // BT.2020 PQ to linear BT.709 nits.
        float3 l = Pq(c);
        nits = float3(
            1.6605 * l.r - 0.5876 * l.g - 0.0728 * l.b,
           -0.1246 * l.r + 1.1329 * l.g - 0.0083 * l.b,
           -0.0182 * l.r - 0.1006 * l.g + 1.1187 * l.b);
    }

    float3 x = max(nits / SdrWhite, 0);
    x = x / (1.0 + x * 0.25);                          // soft shoulder above SDR white
    return pow(saturate(x), 1.0 / 2.2);                // back to display gamma
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)DstSize.x || id.y >= (uint)DstSize.y) return;

    float2 p = float2(id.xy) + 0.5;
    float2 inner = (p - Offset) / Content;
    if (any(inner < 0.0) || any(inner > 1.0))
    {
        Dst[id.xy] = float4(0, 0, 0, 1);
        return;
    }

    float2 foot = SrcSize / Content;                    // source pixels per output pixel
    int2 taps = clamp((int2)ceil(foot * 0.5), 1, 8);   // each bilinear tap covers 2x2
    float2 centre = inner * SrcSize;
    float4 acc = 0;
    [loop] for (int y = 0; y < taps.y; y++)
    {
        [loop] for (int x = 0; x < taps.x; x++)
        {
            float2 o = ((float2(x, y) + 0.5) / float2(taps) - 0.5) * foot;
            acc += Src.SampleLevel(Linear, (centre + o) / SrcSize, 0);
        }
    }

    float3 c = ToSdr(acc.rgb / (taps.x * taps.y));
    Dst[id.xy] = float4(c.b, c.g, c.r, 1);
}
