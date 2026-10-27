using System;

namespace TerrainGame.Terrain;

/// <summary>Classic 3D Perlin gradient noise with a seeded permutation table.</summary>
public sealed class Noise3D
{
    private readonly int[] _perm = new int[512];

    public Noise3D(int seed)
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

    public float Sample(float x, float y, float z)
    {
        var floorX = MathF.Floor(x);
        var floorY = MathF.Floor(y);
        var floorZ = MathF.Floor(z);
        var xi = (int)floorX & 255;
        var yi = (int)floorY & 255;
        var zi = (int)floorZ & 255;
        var xf = x - floorX;
        var yf = y - floorY;
        var zf = z - floorZ;

        var u = Fade(xf);
        var v = Fade(yf);
        var w = Fade(zf);

        var a = _perm[xi] + yi;
        var aa = _perm[a] + zi;
        var ab = _perm[a + 1] + zi;
        var b = _perm[xi + 1] + yi;
        var ba = _perm[b] + zi;
        var bb = _perm[b + 1] + zi;

        var c000 = Gradient(_perm[aa], xf, yf, zf);
        var c100 = Gradient(_perm[ba], xf - 1f, yf, zf);
        var c010 = Gradient(_perm[ab], xf, yf - 1f, zf);
        var c110 = Gradient(_perm[bb], xf - 1f, yf - 1f, zf);
        var c001 = Gradient(_perm[aa + 1], xf, yf, zf - 1f);
        var c101 = Gradient(_perm[ba + 1], xf - 1f, yf, zf - 1f);
        var c011 = Gradient(_perm[ab + 1], xf, yf - 1f, zf - 1f);
        var c111 = Gradient(_perm[bb + 1], xf - 1f, yf - 1f, zf - 1f);

        var x00 = Lerp(c000, c100, u);
        var x10 = Lerp(c010, c110, u);
        var x01 = Lerp(c001, c101, u);
        var x11 = Lerp(c011, c111, u);

        var y0 = Lerp(x00, x10, v);
        var y1 = Lerp(x01, x11, v);

        return Lerp(y0, y1, w);
    }

    private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Gradient(int hash, float x, float y, float z)
    {
        var h = hash & 15;
        var u = h < 8 ? x : y;
        var v = h < 4 ? y : h is 12 or 14 ? x : z;
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }
}
