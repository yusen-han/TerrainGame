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
    public const int ChunkSize = 16;
    public const float VoxelSize = 1f;
    public const int SampleCount = ChunkSize + 1;
    public const int BaseY = -8;

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

        for (var octave = 0; octave < 5; octave++)
        {
            noise += _noise.Sample(worldX * frequency, worldY * frequency, worldZ * frequency) * amplitude;
            amplitude *= 0.5f;
            frequency *= 2.05f;
        }

        noise += _detailNoise.Sample(worldX * 0.11f, worldY * 0.11f, worldZ * 0.11f) * 0.35f;

        return -worldY * 0.55f + noise * 7f;
    }

    public float DensityAt(Vector3 position)
        => BaseDensity(position.X, position.Y, position.Z) + _edits.SampleDelta(position);

    public TerrainVertex[] GenerateChunk(int chunkX, int chunkZ)
    {
        var origin = new Vector3(chunkX * ChunkSize * VoxelSize, BaseY, chunkZ * ChunkSize * VoxelSize);
        var density = new float[SampleCount, SampleCount, SampleCount];

        for (var z = 0; z < SampleCount; z++)
        {
            var worldZ = origin.Z + z * VoxelSize;
            var voxelZ = (int)worldZ;

            for (var x = 0; x < SampleCount; x++)
            {
                var worldX = origin.X + x * VoxelSize;
                var voxelX = (int)worldX;

                for (var y = 0; y < SampleCount; y++)
                {
                    var worldY = origin.Y + y * VoxelSize;
                    var voxelY = (int)worldY;
                    density[x, y, z] = BaseDensity(worldX, worldY, worldZ)
                                       + _edits.GetDelta(voxelX, voxelY, voxelZ);
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
        if (normal.Y < 0.6f || position.Y > 3f)
            return (float)TerrainMaterial.Stone;

        return position.Y > -1f
            ? (float)TerrainMaterial.Dirt
            : (float)TerrainMaterial.Sand;
    }

    public static long ChunkKey(int chunkX, int chunkZ) => ((long)chunkX << 32) ^ (uint)chunkZ;
}
