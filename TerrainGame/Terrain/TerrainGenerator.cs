using System;
using System.Numerics;

namespace TerrainGame.Terrain;

/// <summary>
/// Fully volumetric terrain: a 3D fBm density field sampled into a voxel grid and
/// polygonised with <see cref="MarchingCubes"/>. There is no heightmap; caves,
/// overhangs and floating rock are all directly representable.
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

    private readonly Noise3D _noise;
    private readonly Noise3D _detailNoise;
    private readonly VoxelEdits _edits;

    public TerrainGenerator(int seed, VoxelEdits edits)
    {
        _noise = new Noise3D(seed);
        _detailNoise = new Noise3D(unchecked(seed * 7919 + 13));
        _edits = edits;
    }

    /// <summary>
    /// Density before digging. Positive is solid. The vertical term gives the world
    /// a ground plane; the 3D noise supplies hills, cliffs, overhangs and caves.
    /// </summary>
    public float BaseDensity(float worldX, float worldY, float worldZ)
    {
        var amplitude = 1f;
        var frequency = 0.018f;
        var noise = 0f;

        for (var octave = 0; octave < 6; octave++)
        {
            noise += _noise.Sample(worldX * frequency, worldY * frequency, worldZ * frequency) * amplitude;
            amplitude *= 0.5f;
            frequency *= 2.05f;
        }

        noise += _detailNoise.Sample(worldX * 0.22f, worldY * 0.22f, worldZ * 0.22f) * 0.5f;

        var density = -worldY * 0.55f + noise * 7f;

        // Keep the volume closed at both ends of the chunk: solid deep rock below,
        // air above the mountain line. Without this, noise can punch holes through
        // the bottom/top faces where there is no cap geometry.
        if (worldY < -8f)
            density += (-8f - worldY) * 1.5f;
        else if (worldY > 4f)
            density -= (worldY - 4f) * 3f;

        return density;
    }

    public float DensityAt(Vector3 position)
        => BaseDensity(position.X, position.Y, position.Z) + _edits.SampleDelta(position);

    public TerrainVertex[] GenerateChunk(int chunkX, int chunkZ)
    {
        var origin = new Vector3(chunkX * ChunkWorldSize, BaseY, chunkZ * ChunkWorldSize);
        var density = new float[SamplesX, SamplesY, SamplesZ];

        var voxelX0 = chunkX * CellsX;
        var voxelY0 = (int)MathF.Round(BaseY / VoxelSize);
        var voxelZ0 = chunkZ * CellsZ;

        for (var z = 0; z < SamplesZ; z++)
        {
            var worldZ = origin.Z + z * VoxelSize;

            for (var x = 0; x < SamplesX; x++)
            {
                var worldX = origin.X + x * VoxelSize;

                for (var y = 0; y < SamplesY; y++)
                {
                    var worldY = origin.Y + y * VoxelSize;
                    density[x, y, z] = BaseDensity(worldX, worldY, worldZ)
                                       + _edits.GetDelta(voxelX0 + x, voxelY0 + y, voxelZ0 + z);
                }
            }
        }

        var vertices = MarchingCubes.Generate(density, origin, VoxelSize);

        for (var i = 0; i < vertices.Length; i++)
            vertices[i].Material = SelectMaterial(vertices[i].Position, vertices[i].Normal);

        return vertices;
    }

    private static float SelectMaterial(Vector3 position, Vector3 normal)
    {
        if (normal.Y < 0.6f || position.Y > 5f)
            return (float)TerrainMaterial.Stone;

        if (position.Y < -1.5f)
            return (float)TerrainMaterial.Sand;

        return normal.Y > 0.8f
            ? (float)TerrainMaterial.Grass
            : (float)TerrainMaterial.Dirt;
    }

    public static long ChunkKey(int chunkX, int chunkZ) => ((long)chunkX << 32) ^ (uint)chunkZ;
}
