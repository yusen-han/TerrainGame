using System;

namespace TerrainGame.Terrain;

/// <summary>
/// Dense storage for one terrain chunk: a 3D density grid plus a per-voxel
/// material grid. Digging writes straight into these arrays; nothing stores a
/// separate edit overlay.
/// </summary>
public sealed class ChunkData
{
    public const int SizeX = TerrainGenerator.SamplesX;
    public const int SizeY = TerrainGenerator.SamplesY;
    public const int SizeZ = TerrainGenerator.SamplesZ;

    public readonly float[,,] Density = new float[SizeX, SizeY, SizeZ];
    public readonly byte[,,] Material = new byte[SizeX, SizeY, SizeZ];
    public readonly object Sync = new();

    public readonly int ChunkX;
    public readonly int ChunkZ;

    /// <summary>True once digging has touched the grid; such chunks survive streaming unload.</summary>
    public bool Modified;

    public ChunkData(int chunkX, int chunkZ)
    {
        ChunkX = chunkX;
        ChunkZ = chunkZ;
    }

    public static readonly int SampleCount = SizeX * SizeY * SizeZ;
}
