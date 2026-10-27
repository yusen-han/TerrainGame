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
};

struct VSOutput
{
    float4 ClipPosition  : SV_Position;
    float3 Normal        : NORMAL;
    float3 WorldPosition : TEXCOORD0;
};

VSOutput VSMain(VSInput input)
{
    VSOutput output;
    output.ClipPosition = mul(float4(input.Position, 1.0), ViewProjection);
    output.Normal = input.Normal;
    output.WorldPosition = input.Position;
    return output;
}

float4 PSMain(VSOutput input) : SV_Target
{
    float3 normal = normalize(input.Normal);
    float3 lightDirection = normalize(float3(0.45, 1.0, 0.25));
    float3 viewDirection = normalize(CameraPosition.xyz - input.WorldPosition);

    float diffuse = saturate(dot(normal, lightDirection));
    float rim = pow(1.0 - saturate(dot(normal, viewDirection)), 3.0);

    float3 grass = float3(0.22, 0.42, 0.16);
    float3 rock  = float3(0.42, 0.40, 0.38);
    float3 snow  = float3(0.92, 0.94, 0.97);

    float steepness = saturate((1.0 - normal.y) * 2.4);
    float3 color = lerp(grass, rock, steepness);
    color = lerp(color, snow, saturate((input.WorldPosition.y - 3.5) / 3.0));

    float3 lighting = 0.32 + 0.68 * diffuse;
    color = color * lighting + rim * 0.05;

    // Distance fog: blends distant chunks into the sky and hides stream-in edges.
    float distanceToCamera = length(CameraPosition.xyz - input.WorldPosition);
    float fog = saturate((distanceToCamera - 55.0) / 50.0);
    float3 fogColor = float3(0.52, 0.68, 0.86);
    color = lerp(color, fogColor, fog);

    return float4(saturate(color), 1.0);
}
