using System;
using System.Collections.Concurrent;
using System.Numerics;

namespace TerrainGame.Terrain;

/// <summary>
/// Persistent sparse edits to the volumetric density field. A dig subtracts
/// density from every voxel sample inside the brush, which carves both surface
/// craters and fully enclosed caves. Samples live in world-voxel coordinates so
/// they survive chunk unload/reload.
/// </summary>
public sealed class VoxelEdits
{
    private readonly ConcurrentDictionary<(int X, int Y, int Z), float> _deltas = new();

    public int Count => _deltas.Count;

    /// <summary>Applies a smooth spherical subtraction centred on a world position.</summary>
    public void ApplyBrush(Vector3 center, float radius, float strength)
    {
        var minX = (int)MathF.Floor(center.X - radius);
        var maxX = (int)MathF.Ceiling(center.X + radius);
        var minY = (int)MathF.Floor(center.Y - radius);
        var maxY = (int)MathF.Ceiling(center.Y + radius);
        var minZ = (int)MathF.Floor(center.Z - radius);
        var maxZ = (int)MathF.Ceiling(center.Z + radius);
        var radiusSquared = radius * radius;

        for (var z = minZ; z <= maxZ; z++)
        {
            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var dx = x - center.X;
                    var dy = y - center.Y;
                    var dz = z - center.Z;
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
        var x0 = (int)MathF.Floor(position.X);
        var y0 = (int)MathF.Floor(position.Y);
        var z0 = (int)MathF.Floor(position.Z);
        var fx = position.X - x0;
        var fy = position.Y - y0;
        var fz = position.Z - z0;

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
