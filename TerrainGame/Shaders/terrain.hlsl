// Terrain shaders (HLSL 6.0, compiled to SPIR-V with dxc).
//
// Regenerate:
//   dxc -spirv -T vs_6_0 -E VSMain -Zpr -Fo terrain.vert.spv terrain.hlsl
//   dxc -spirv -T ps_6_0 -E PSMain -Zpr -Fo terrain.frag.spv terrain.hlsl

cbuffer Scene : register(b0)
{
    row_major float4x4 ViewProjection;
    float4 CameraPosition;
};

// Layer order matches TerrainMaterial: grass, sand, dirt, stone.
Texture2DArray TerrainTextures : register(t1);
SamplerState TerrainSampler : register(s2);

struct VSInput
{
    float3 Position : POSITION;
    float3 Normal   : NORMAL;
    float  Material : MATERIAL;
};

struct VSOutput
{
    float4 ClipPosition  : SV_Position;
    float3 Normal        : NORMAL;
    float3 WorldPosition : TEXCOORD0;
    nointerpolation float Material : TEXCOORD1;
};

VSOutput VSMain(VSInput input)
{
    VSOutput output;
    output.ClipPosition = mul(float4(input.Position, 1.0), ViewProjection);
    output.Normal = input.Normal;
    output.WorldPosition = input.Position;
    output.Material = input.Material;
    return output;
}

float3 SampleTriplanar(float3 position, float3 normal, float layer)
{
    float3 blend = pow(abs(normal), 4.0);
    blend /= max(blend.x + blend.y + blend.z, 1e-4);

    const float scale = 0.12;

    float3 x = TerrainTextures.Sample(TerrainSampler, float3(position.zy * scale, layer)).rgb;
    float3 y = TerrainTextures.Sample(TerrainSampler, float3(position.xz * scale, layer)).rgb;
    float3 z = TerrainTextures.Sample(TerrainSampler, float3(position.xy * scale, layer)).rgb;

    return x * blend.x + y * blend.y + z * blend.z;
}

float4 PSMain(VSOutput input) : SV_Target
{
    float3 normal = normalize(input.Normal);
    float3 lightDirection = normalize(float3(0.45, 1.0, 0.25));
    float3 viewDirection = normalize(CameraPosition.xyz - input.WorldPosition);

    float diffuse = saturate(dot(normal, lightDirection));
    float rim = pow(1.0 - saturate(dot(normal, viewDirection)), 3.0);

    float layer = round(input.Material);
    float3 albedo = SampleTriplanar(input.WorldPosition, normal, layer);

    float3 lighting = 0.32 + 0.68 * diffuse;
    float3 color = albedo * lighting + rim * 0.03;

    // Distance fog: blends distant chunks into the sky and hides stream-in edges.
    float distanceToCamera = length(CameraPosition.xyz - input.WorldPosition);
    float fog = saturate((distanceToCamera - 55.0) / 50.0);
    float3 fogColor = float3(0.52, 0.68, 0.86);
    color = lerp(color, fogColor, fog);

    return float4(saturate(color), 1.0);
}
