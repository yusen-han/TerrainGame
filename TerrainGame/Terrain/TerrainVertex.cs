using System;
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

/// <summary>
/// Classic 2D Perlin gradient noise with a seeded permutation table.
/// </summary>
public sealed class Noise2D
{
    private readonly int[] _perm = new int[512];

    public Noise2D(int seed)
    {
        var values = new int[256];
        for (var i = 0; i < values.Length; i++)
            values[i] = i;

        var rng = new Random(seed);
        for (var i = values.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }

        for (var i = 0; i < _perm.Length; i++)
            _perm[i] = values[i & 255];
    }

    public float Sample(float x, float y)
    {
        var floorX = MathF.Floor(x);
        var floorY = MathF.Floor(y);
        var xi = (int)floorX & 255;
        var yi = (int)floorY & 255;
        var xf = x - floorX;
        var yf = y - floorY;

        var u = Fade(xf);
        var v = Fade(yf);

        var aa = _perm[_perm[xi] + yi];
        var ab = _perm[_perm[xi] + yi + 1];
        var ba = _perm[_perm[xi + 1] + yi];
        var bb = _perm[_perm[xi + 1] + yi + 1];

        var x1 = Lerp(Gradient(aa, xf, yf), Gradient(ba, xf - 1f, yf), u);
        var x2 = Lerp(Gradient(ab, xf, yf - 1f), Gradient(bb, xf - 1f, yf - 1f), u);
        return Lerp(x1, x2, v);
    }

    private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Gradient(int hash, float x, float y)
    {
        switch (hash & 7)
        {
            case 0: return x + y;
            case 1: return -x + y;
            case 2: return x - y;
            case 3: return -x - y;
            case 4: return x;
            case 5: return -x;
            case 6: return y;
            default: return -y;
        }
    }
}
