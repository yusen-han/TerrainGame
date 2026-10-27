using System;
using System.Numerics;

namespace TerrainGame.Terrain;

/// <summary>
/// In-house procedural terrain: fBm Perlin heightfield sampled into a 3D density
/// grid, polygonised with <see cref="MarchingCubes"/>.
/// </summary>
public sealed class TerrainGenerator
{
    public const int ChunkSize = 16;
    public const float VoxelSize = 1f;
    public const int SampleCount = ChunkSize + 1;
    public const int BaseY = -8;

    private readonly Noise2D _baseNoise;
    private readonly Noise2D _detailNoise;

    public TerrainGenerator(int seed)
    {
        _baseNoise = new Noise2D(seed);
        _detailNoise = new Noise2D(unchecked(seed * 7919 + 13));
    }

    public float Height(float worldX, float worldZ)
    {
        var amplitude = 1f;
        var frequency = 0.009f;
        var height = 0f;

        for (var octave = 0; octave < 6; octave++)
        {
            height += _baseNoise.Sample(worldX * frequency, worldZ * frequency) * amplitude;
            amplitude *= 0.5f;
            frequency *= 2.0f;
        }

        height *= 7f;
        height += _detailNoise.Sample(worldX * 0.07f, worldZ * 0.07f) * 1.5f;

        // Soft-clamp so the surface always stays inside the chunk's vertical range.
        const float limit = 7.6f;
        return limit * MathF.Tanh(height / limit);
    }

    public TerrainVertex[] GenerateChunk(int chunkX, int chunkZ)
    {
        var origin = new Vector3(chunkX * ChunkSize * VoxelSize, BaseY, chunkZ * ChunkSize * VoxelSize);
        var density = new float[SampleCount, SampleCount, SampleCount];

        for (var z = 0; z < SampleCount; z++)
        {
            for (var x = 0; x < SampleCount; x++)
            {
                var height = Height(origin.X + x * VoxelSize, origin.Z + z * VoxelSize);
                for (var y = 0; y < SampleCount; y++)
                    density[x, y, z] = height - (origin.Y + y * VoxelSize);
            }
        }

        return MarchingCubes.Generate(density, origin, VoxelSize);
    }

    public static long ChunkKey(int chunkX, int chunkZ) => ((long)chunkX << 32) ^ (uint)chunkZ;
}
