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

float3 MaterialColor(float material)
{
    if (material < 0.5)
        return float3(0.78, 0.71, 0.50); // sand

    if (material < 1.5)
        return float3(0.42, 0.31, 0.20); // dirt

    return float3(0.47, 0.47, 0.50);     // stone
}

float4 PSMain(VSOutput input) : SV_Target
{
    float3 normal = normalize(input.Normal);
    float3 lightDirection = normalize(float3(0.45, 1.0, 0.25));
    float3 viewDirection = normalize(CameraPosition.xyz - input.WorldPosition);

    float diffuse = saturate(dot(normal, lightDirection));
    float rim = pow(1.0 - saturate(dot(normal, viewDirection)), 3.0);

    float3 color = MaterialColor(input.Material);
    float3 lighting = 0.32 + 0.68 * diffuse;
    color = color * lighting + rim * 0.05;

    // Distance fog: blends distant chunks into the sky and hides stream-in edges.
    float distanceToCamera = length(CameraPosition.xyz - input.WorldPosition);
    float fog = saturate((distanceToCamera - 55.0) / 50.0);
    float3 fogColor = float3(0.52, 0.68, 0.86);
    color = lerp(color, fogColor, fog);

    return float4(saturate(color), 1.0);
}
