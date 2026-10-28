using System;
using System.Numerics;

namespace TerrainGame.Terrain;

/// <summary>
/// Terrain generation: a 2D eroded height field is filled into a dense 3D
/// density grid (positive = solid), with a small amount of carved caves and a
/// per-voxel material grid. Storage is handled by <see cref="ChunkData"/>.
/// </summary>
public sealed class TerrainGenerator
{
    // 32 x 48 x 32 cells at 0.5 units = a 16 x 24 x 16 world-unit chunk.
    public const int CellsX = 32;
    public const int CellsY = 48;
    public const int CellsZ = 32;
    public const float VoxelSize = 0.5f;
    public const int SamplesX = CellsX + 1;
    public const int SamplesY = CellsY + 1;
    public const int SamplesZ = CellsZ + 1;
    public const int BaseY = -16;
    public const float ChunkWorldSize = CellsX * VoxelSize;

    private const float CaveThreshold = 0.55f;
    private const float CaveStrength = 30f;
    private const float CaveGateNear = 3f;
    private const float CaveGateFar = 7f;

    private readonly TerrainHeightField _heights;
    private readonly Noise3D _caveNoise;

    public TerrainGenerator(int seed)
    {
        _heights = new TerrainHeightField(seed);
        _caveNoise = new Noise3D(unchecked(seed * 7919 + 13));
    }

    public float HeightAt(float worldX, float worldZ) => _heights.HeightAt(worldX, worldZ);

    /// <summary>Fills a chunk's density and material arrays from the eroded height field.</summary>
    public void FillChunk(ChunkData data, int chunkX, int chunkZ)
    {
        var originX = chunkX * ChunkWorldSize;
        var originZ = chunkZ * ChunkWorldSize;

        for (var z = 0; z < SamplesZ; z++)
        {
            var worldZ = originZ + z * VoxelSize;

            for (var x = 0; x < SamplesX; x++)
            {
                var worldX = originX + x * VoxelSize;
                var surface = HeightAt(worldX, worldZ);
                var slope = SlopeAt(worldX, worldZ);

                for (var y = 0; y < SamplesY; y++)
                {
                    var worldY = BaseY + y * VoxelSize;
                    var depth = surface - worldY;
                    var density = depth;

                    if (depth > 0f)
                    {
                        var cave = _caveNoise.Sample(worldX * 0.045f, worldY * 0.045f, worldZ * 0.045f);
                        if (cave > CaveThreshold)
                        {
                            var carve = (cave - CaveThreshold) * CaveStrength;
                            density -= carve * SmoothStep(CaveGateNear, CaveGateFar, depth);
                        }
                    }

                    data.Density[x, y, z] = density;
                    data.Material[x, y, z] = (byte)SelectMaterial(depth, worldY, slope);
                }
            }
        }
    }

    /// <summary>Meshes a chunk from its stored arrays. Caller must hold <see cref="ChunkData.Sync"/>.</summary>
    public TerrainVertex[] BuildMesh(ChunkData data, int chunkX, int chunkZ)
    {
        var origin = new Vector3(chunkX * ChunkWorldSize, BaseY, chunkZ * ChunkWorldSize);
        return MarchingCubes.Generate(data.Density, data.Material, origin, VoxelSize);
    }

    private float SlopeAt(float worldX, float worldZ)
    {
        const float delta = 1f;
        var dx = HeightAt(worldX + delta, worldZ) - HeightAt(worldX - delta, worldZ);
        var dz = HeightAt(worldX, worldZ + delta) - HeightAt(worldX, worldZ - delta);
        return MathF.Sqrt(dx * dx + dz * dz) / (2f * delta);
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        var t = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static TerrainMaterial SelectMaterial(float depth, float worldY, float slope)
    {
        if (slope > 0.75f || depth > 8f)
            return TerrainMaterial.Stone;

        if (depth < 1.5f)
            return worldY < -1.5f ? TerrainMaterial.Sand : TerrainMaterial.Grass;

        return TerrainMaterial.Dirt;
    }

    public static long ChunkKey(int chunkX, int chunkZ) => ((long)chunkX << 32) ^ (uint)chunkZ;
}
