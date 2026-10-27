using System;
using System.Collections.Concurrent;
using System.Numerics;

namespace TerrainGame.Terrain;

/// <summary>
/// Persistent sparse edits to the volumetric density field. Keys are global voxel
/// indices (world position / <see cref="TerrainGenerator.VoxelSize"/>). A dig
/// subtracts density from every voxel inside the brush, which carves both surface
/// craters and fully enclosed caves; samples survive chunk unload/reload.
/// </summary>
public sealed class VoxelEdits
{
    private const float Size = TerrainGenerator.VoxelSize;

    private readonly ConcurrentDictionary<(int X, int Y, int Z), float> _deltas = new();

    public int Count => _deltas.Count;

    /// <summary>Applies a smooth spherical subtraction centred on a world position.</summary>
    public void ApplyBrush(Vector3 center, float radius, float strength)
    {
        var minX = (int)MathF.Floor((center.X - radius) / Size);
        var maxX = (int)MathF.Ceiling((center.X + radius) / Size);
        var minY = (int)MathF.Floor((center.Y - radius) / Size);
        var maxY = (int)MathF.Ceiling((center.Y + radius) / Size);
        var minZ = (int)MathF.Floor((center.Z - radius) / Size);
        var maxZ = (int)MathF.Ceiling((center.Z + radius) / Size);
        var radiusSquared = radius * radius;

        for (var z = minZ; z <= maxZ; z++)
        {
            var worldZ = z * Size;
            for (var y = minY; y <= maxY; y++)
            {
                var worldY = y * Size;
                for (var x = minX; x <= maxX; x++)
                {
                    var worldX = x * Size;
                    var dx = worldX - center.X;
                    var dy = worldY - center.Y;
                    var dz = worldZ - center.Z;
                    var distanceSquared = dx * dx + dy * dy + dz * dz;
                    if (distanceSquared >= radiusSquared)
                        continue;

                    var t = 1f - MathF.Sqrt(distanceSquared) / radius;
                    var delta = strength * t * t;
                    _deltas.AddOrUpdate((x, y, z), -delta, (_, existing) => existing - delta);
                }
            }
        }
    }

    public float GetDelta(int x, int y, int z)
        => _deltas.TryGetValue((x, y, z), out var delta) ? delta : 0f;

    /// <summary>Trilinear sample of the edit field, used by the dig raycast.</summary>
    public float SampleDelta(Vector3 position)
    {
        var vx = position.X / Size;
        var vy = position.Y / Size;
        var vz = position.Z / Size;

        var x0 = (int)MathF.Floor(vx);
        var y0 = (int)MathF.Floor(vy);
        var z0 = (int)MathF.Floor(vz);
        var fx = vx - x0;
        var fy = vy - y0;
        var fz = vz - z0;

        var c000 = GetDelta(x0, y0, z0);
        var c100 = GetDelta(x0 + 1, y0, z0);
        var c010 = GetDelta(x0, y0 + 1, z0);
        var c110 = GetDelta(x0 + 1, y0 + 1, z0);
        var c001 = GetDelta(x0, y0, z0 + 1);
        var c101 = GetDelta(x0 + 1, y0, z0 + 1);
        var c011 = GetDelta(x0, y0 + 1, z0 + 1);
        var c111 = GetDelta(x0 + 1, y0 + 1, z0 + 1);

        var x00 = Lerp(c000, c100, fx);
        var x10 = Lerp(c010, c110, fx);
        var x01 = Lerp(c001, c101, fx);
        var x11 = Lerp(c011, c111, fx);

        var y0Lerp = Lerp(x00, x10, fy);
        var y1Lerp = Lerp(x01, x11, fy);

        return Lerp(y0Lerp, y1Lerp, fz);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
