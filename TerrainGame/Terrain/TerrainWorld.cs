using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;

namespace TerrainGame.Terrain;

/// <summary>
/// Owns the dense chunk grids. Digging writes straight into
/// <see cref="ChunkData.Density"/> — there is no separate edit overlay.
/// Modified chunks are kept when streaming unloads them.
/// </summary>
public sealed class TerrainWorld
{
    private readonly TerrainGenerator _generator;
    private readonly ConcurrentDictionary<long, ChunkData> _loaded = new();
    private readonly ConcurrentDictionary<long, ChunkData> _modified = new();

    public TerrainWorld(int seed)
    {
        _generator = new TerrainGenerator(seed);
    }

    public int LoadedChunkCount => _loaded.Count;

    public int ModifiedChunkCount => _modified.Count;

    public ChunkData GetOrCreate(int chunkX, int chunkZ)
    {
        var key = TerrainGenerator.ChunkKey(chunkX, chunkZ);

        return _loaded.GetOrAdd(key, _ =>
        {
            if (_modified.TryGetValue(key, out var persisted))
                return persisted;

            var data = new ChunkData(chunkX, chunkZ);
            _generator.FillChunk(data, chunkX, chunkZ);
            return data;
        });
    }

    public TerrainVertex[] BuildMesh(int chunkX, int chunkZ)
    {
        var data = GetOrCreate(chunkX, chunkZ);
        lock (data.Sync)
            return _generator.BuildMesh(data, chunkX, chunkZ);
    }

    /// <summary>Drops a chunk; modified grids are retained so digging survives streaming.</summary>
    public void Release(int chunkX, int chunkZ)
    {
        var key = TerrainGenerator.ChunkKey(chunkX, chunkZ);
        if (_loaded.TryRemove(key, out var data) && data.Modified)
            _modified[key] = data;
    }

    /// <summary>Trilinear sample of the stored density grid.</summary>
    public float DensityAt(Vector3 position)
    {
        var vx = position.X / TerrainGenerator.VoxelSize;
        var vy = position.Y / TerrainGenerator.VoxelSize;
        var vz = position.Z / TerrainGenerator.VoxelSize;

        var x0 = (int)MathF.Floor(vx);
        var y0 = (int)MathF.Floor(vy);
        var z0 = (int)MathF.Floor(vz);
        var fx = vx - x0;
        var fy = vy - y0;
        var fz = vz - z0;

        var c000 = GridDensity(x0, y0, z0);
        var c100 = GridDensity(x0 + 1, y0, z0);
        var c010 = GridDensity(x0, y0 + 1, z0);
        var c110 = GridDensity(x0 + 1, y0 + 1, z0);
        var c001 = GridDensity(x0, y0, z0 + 1);
        var c101 = GridDensity(x0 + 1, y0, z0 + 1);
        var c011 = GridDensity(x0, y0 + 1, z0 + 1);
        var c111 = GridDensity(x0 + 1, y0 + 1, z0 + 1);

        var x00 = Lerp(c000, c100, fx);
        var x10 = Lerp(c010, c110, fx);
        var x01 = Lerp(c001, c101, fx);
        var x11 = Lerp(c011, c111, fx);

        var y0Lerp = Lerp(x00, x10, fy);
        var y1Lerp = Lerp(x01, x11, fy);

        return Lerp(y0Lerp, y1Lerp, fz);
    }

    private float GridDensity(int ix, int iy, int iz)
    {
        var chunkX = (int)MathF.Floor(ix / (float)TerrainGenerator.CellsX);
        var chunkZ = (int)MathF.Floor(iz / (float)TerrainGenerator.CellsZ);
        var localX = ix - chunkX * TerrainGenerator.CellsX;
        var localZ = iz - chunkZ * TerrainGenerator.CellsZ;
        var localY = Math.Clamp(iy - VoxelIndexBaseY, 0, TerrainGenerator.SamplesY - 1);

        var data = GetOrCreate(chunkX, chunkZ);
        lock (data.Sync)
            return data.Density[localX, localY, localZ];
    }

    private static int VoxelIndexBaseY => (int)MathF.Round(TerrainGenerator.BaseY / TerrainGenerator.VoxelSize);

    /// <summary>
    /// Carves a spherical brush into the density grids of every affected chunk.
    /// Returns the chunk keys that were touched (they need a remesh).
    /// </summary>
    public List<long> ApplyBrush(Vector3 center, float radius, float strength)
    {
        var touched = new List<long>();
        if (radius <= 0f)
            return touched;

        var chunkWorldSize = TerrainGenerator.ChunkWorldSize;
        var minChunkX = (int)MathF.Floor((center.X - radius) / chunkWorldSize);
        var maxChunkX = (int)MathF.Floor((center.X + radius) / chunkWorldSize);
        var minChunkZ = (int)MathF.Floor((center.Z - radius) / chunkWorldSize);
        var maxChunkZ = (int)MathF.Floor((center.Z + radius) / chunkWorldSize);
        var radiusSquared = radius * radius;

        for (var chunkZ = minChunkZ; chunkZ <= maxChunkZ; chunkZ++)
        {
            for (var chunkX = minChunkX; chunkX <= maxChunkX; chunkX++)
            {
                var data = GetOrCreate(chunkX, chunkZ);
                var changed = false;

                lock (data.Sync)
                {
                    var originX = chunkX * chunkWorldSize;
                    var originZ = chunkZ * chunkWorldSize;

                    for (var z = 0; z < TerrainGenerator.SamplesZ; z++)
                    {
                        var worldZ = originZ + z * TerrainGenerator.VoxelSize;
                        if (worldZ < center.Z - radius || worldZ > center.Z + radius)
                            continue;

                        for (var x = 0; x < TerrainGenerator.SamplesX; x++)
                        {
                            var worldX = originX + x * TerrainGenerator.VoxelSize;
                            if (worldX < center.X - radius || worldX > center.X + radius)
                                continue;

                            for (var y = 0; y < TerrainGenerator.SamplesY; y++)
                            {
                                var worldY = TerrainGenerator.BaseY + y * TerrainGenerator.VoxelSize;
                                var dx = worldX - center.X;
                                var dy = worldY - center.Y;
                                var dz = worldZ - center.Z;
                                var distanceSquared = dx * dx + dy * dy + dz * dz;
                                if (distanceSquared >= radiusSquared)
                                    continue;

                                var falloff = 1f - MathF.Sqrt(distanceSquared) / radius;
                                data.Density[x, y, z] -= strength * falloff * falloff;
                                changed = true;
                            }
                        }
                    }

                    if (changed)
                        data.Modified = true;
                }

                if (changed)
                    touched.Add(TerrainGenerator.ChunkKey(chunkX, chunkZ));
            }
        }

        return touched;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
