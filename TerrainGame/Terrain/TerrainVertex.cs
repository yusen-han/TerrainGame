using System.Numerics;
using System.Runtime.InteropServices;

namespace TerrainGame.Terrain;

[StructLayout(LayoutKind.Sequential)]
public struct TerrainVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public float Material;

    public TerrainVertex(Vector3 position, Vector3 normal, float material = 0f)
    {
        Position = position;
        Normal = normal;
        Material = material;
    }

    public const int SizeInBytes = 28;
}
