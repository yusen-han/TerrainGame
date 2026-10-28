using System;
using System.Collections.Concurrent;
using System.Threading;

namespace TerrainGame.Terrain;

/// <summary>
/// World-space height field built from 2D fBm and refined with rain (droplet)
/// erosion. Tiles are world-aligned and cached; sampling blends neighbouring
/// tiles along their borders so chunk meshes never crack.
/// </summary>
public sealed class TerrainHeightField
{
    public const float TileSize = 64f;
    public const float CellSize = 1f;
    public const int Cells = 64;
    public const int Pad = 16;
    public const int GridCells = Cells + Pad * 2;
    private const int GridSamples = GridCells + 1;
    private const float BlendWidth = 8f;

    private const int DropletStride = 2;
    private const int DropletLifetime = 30;
    private const float Inertia = 0.05f;
    private const float CapacityFactor = 4f;
    private const float MinCapacity = 0.01f;
    private const float ErodeSpeed = 0.3f;
    private const float DepositSpeed = 0.3f;
    private const float Gravity = 4f;
    private const float Evaporate = 0.01f;

    private readonly Noise2D _noise;
    private readonly ConcurrentDictionary<long, Lazy<Tile>> _tiles = new();

    public TerrainHeightField(int seed)
    {
        _noise = new Noise2D(seed);
    }

    /// <summary>Eroded terrain height at a world position.</summary>
    public float HeightAt(float worldX, float worldZ)
    {
        var tileX = (int)MathF.Floor(worldX / TileSize);
        var tileZ = (int)MathF.Floor(worldZ / TileSize);
        var localX = worldX - tileX * TileSize;
        var localZ = worldZ - tileZ * TileSize;

        var blendX = 0f;
        var blendZ = 0f;
        var neighbourX = tileX;
        var neighbourZ = tileZ;

        if (localX < BlendWidth)
        {
            neighbourX = tileX - 1;
            blendX = 0.5f * (1f - localX / BlendWidth);
        }
        else if (localX > TileSize - BlendWidth)
        {
            neighbourX = tileX + 1;
            blendX = 0.5f * (1f - (TileSize - localX) / BlendWidth);
        }

        if (localZ < BlendWidth)
        {
            neighbourZ = tileZ - 1;
            blendZ = 0.5f * (1f - localZ / BlendWidth);
        }
        else if (localZ > TileSize - BlendWidth)
        {
            neighbourZ = tileZ + 1;
            blendZ = 0.5f * (1f - (TileSize - localZ) / BlendWidth);
        }

        var h00 = SampleTile(tileX, tileZ, worldX, worldZ);
        if (blendX == 0f && blendZ == 0f)
            return h00;

        var h01 = blendZ > 0f ? SampleTile(tileX, neighbourZ, worldX, worldZ) : h00;
        var h10 = blendX > 0f ? SampleTile(neighbourX, tileZ, worldX, worldZ) : h00;
        var h11 = blendX > 0f && blendZ > 0f
            ? SampleTile(neighbourX, neighbourZ, worldX, worldZ)
            : h10;

        var x0 = h00 + (h10 - h00) * blendX;
        var x1 = h01 + (h11 - h01) * blendX;
        return x0 + (x1 - x0) * blendZ;
    }

    private float SampleTile(int tileX, int tileZ, float worldX, float worldZ)
    {
        var tile = GetTile(tileX, tileZ);
        var gx = (worldX - tile.OriginX) / CellSize;
        var gz = (worldZ - tile.OriginZ) / CellSize;
        return SampleBilinear(tile, gx, gz);
    }

    private Tile GetTile(int tileX, int tileZ)
    {
        var key = ((long)tileX << 32) ^ (uint)tileZ;
        var lazy = _tiles.GetOrAdd(
            key,
            _ => new Lazy<Tile>(() => BuildTile(tileX, tileZ), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    private Tile BuildTile(int tileX, int tileZ)
    {
        var tile = new Tile
        {
            OriginX = tileX * TileSize - Pad * CellSize,
            OriginZ = tileZ * TileSize - Pad * CellSize,
            Heights = new float[GridSamples * GridSamples],
        };

        for (var z = 0; z < GridSamples; z++)
        {
            var worldZ = tile.OriginZ + z * CellSize;
            for (var x = 0; x < GridSamples; x++)
            {
                var worldX = tile.OriginX + x * CellSize;
                tile.Heights[z * GridSamples + x] = BaseHeight(worldX, worldZ);
            }
        }

        Erode(tile);

        for (var i = 0; i < tile.Heights.Length; i++)
            tile.Heights[i] = Math.Clamp(tile.Heights[i], -12f, 6f);

        return tile;
    }

    private float BaseHeight(float worldX, float worldZ)
    {
        var amplitude = 1f;
        var frequency = 0.010f;
        var noise = 0f;

        for (var octave = 0; octave < 5; octave++)
        {
            noise += _noise.Sample(worldX * frequency, worldZ * frequency) * amplitude;
            amplitude *= 0.5f;
            frequency *= 2.03f;
        }

        return 8f * MathF.Tanh(noise * 1.2f);
    }

    private void Erode(Tile tile)
    {
        for (var gz = Pad; gz < GridCells - Pad; gz += DropletStride)
        {
            for (var gx = Pad; gx < GridCells - Pad; gx += DropletStride)
            {
                var worldX = tile.OriginX + gx * CellSize;
                var worldZ = tile.OriginZ + gz * CellSize;
                SimulateDroplet(tile, gx, gz, WorldSeed(worldX, worldZ));
            }
        }
    }

    private static void SimulateDroplet(Tile tile, int startX, int startZ, int seed)
    {
        var rng = new Random(seed);
        var posX = startX + (float)rng.NextDouble();
        var posZ = startZ + (float)rng.NextDouble();
        var dirX = 0f;
        var dirZ = 0f;
        var speed = 1f;
        var water = 1f;
        var sediment = 0f;

        for (var step = 0; step < DropletLifetime; step++)
        {
            if (posX < 0f || posZ < 0f || posX >= GridCells || posZ >= GridCells)
                break;

            var (height, gradientX, gradientZ) = SampleWithGradient(tile, posX, posZ);

            dirX = dirX * Inertia - gradientX * (1f - Inertia);
            dirZ = dirZ * Inertia - gradientZ * (1f - Inertia);

            var length = MathF.Sqrt(dirX * dirX + dirZ * dirZ);
            if (length < 1e-6f)
                break;

            dirX /= length;
            dirZ /= length;

            var nextX = posX + dirX;
            var nextZ = posZ + dirZ;

            if (nextX < 0f || nextZ < 0f || nextX >= GridCells || nextZ >= GridCells)
                break;

            var nextHeight = SampleBilinear(tile, nextX, nextZ);
            var deltaHeight = nextHeight - height;
            var capacity = MathF.Max(-deltaHeight * speed * water * CapacityFactor, MinCapacity);

            if (sediment > capacity || deltaHeight > 0f)
            {
                var deposit = deltaHeight > 0f
                    ? MathF.Min(deltaHeight, sediment)
                    : (sediment - capacity) * DepositSpeed;
                sediment -= deposit;
                Deposit(tile, posX, posZ, deposit);
            }
            else
            {
                var erode = MathF.Min((capacity - sediment) * ErodeSpeed, -deltaHeight);
                sediment += erode;
                Deposit(tile, posX, posZ, -erode);
            }

            speed = MathF.Sqrt(MathF.Max(0f, speed * speed - deltaHeight * Gravity));
            water *= 1f - Evaporate;

            posX = nextX;
            posZ = nextZ;
        }
    }

    private static float SampleBilinear(Tile tile, float gx, float gz)
    {
        var x0 = Math.Clamp((int)MathF.Floor(gx), 0, GridCells - 1);
        var z0 = Math.Clamp((int)MathF.Floor(gz), 0, GridCells - 1);
        var tx = Math.Clamp(gx - x0, 0f, 1f);
        var tz = Math.Clamp(gz - z0, 0f, 1f);

        var i = z0 * GridSamples + x0;
        var h00 = tile.Heights[i];
        var h10 = tile.Heights[i + 1];
        var h01 = tile.Heights[i + GridSamples];
        var h11 = tile.Heights[i + GridSamples + 1];

        var top = h00 + (h10 - h00) * tx;
        var bottom = h01 + (h11 - h01) * tx;
        return top + (bottom - top) * tz;
    }

    private static (float Height, float GradientX, float GradientZ) SampleWithGradient(Tile tile, float gx, float gz)
    {
        var x0 = Math.Clamp((int)MathF.Floor(gx), 0, GridCells - 1);
        var z0 = Math.Clamp((int)MathF.Floor(gz), 0, GridCells - 1);
        var tx = Math.Clamp(gx - x0, 0f, 1f);
        var tz = Math.Clamp(gz - z0, 0f, 1f);

        var i = z0 * GridSamples + x0;
        var h00 = tile.Heights[i];
        var h10 = tile.Heights[i + 1];
        var h01 = tile.Heights[i + GridSamples];
        var h11 = tile.Heights[i + GridSamples + 1];

        var top = h00 + (h10 - h00) * tx;
        var bottom = h01 + (h11 - h01) * tx;
        var height = top + (bottom - top) * tz;

        var gradientX = (h10 - h00) * (1f - tz) + (h11 - h01) * tz;
        var gradientZ = (h01 - h00) * (1f - tx) + (h11 - h10) * tx;

        return (height, gradientX / CellSize, gradientZ / CellSize);
    }

    private static void Deposit(Tile tile, float gx, float gz, float amount)
    {
        if (amount == 0f)
            return;

        var x0 = Math.Clamp((int)MathF.Floor(gx), 0, GridCells - 1);
        var z0 = Math.Clamp((int)MathF.Floor(gz), 0, GridCells - 1);
        var tx = Math.Clamp(gx - x0, 0f, 1f);
        var tz = Math.Clamp(gz - z0, 0f, 1f);

        var i = z0 * GridSamples + x0;
        tile.Heights[i] += amount * (1f - tx) * (1f - tz);
        tile.Heights[i + 1] += amount * tx * (1f - tz);
        tile.Heights[i + GridSamples] += amount * (1f - tx) * tz;
        tile.Heights[i + GridSamples + 1] += amount * tx * tz;
    }

    private static int WorldSeed(float worldX, float worldZ)
    {
        unchecked
        {
            var ix = (int)MathF.Round(worldX * 4f);
            var iz = (int)MathF.Round(worldZ * 4f);
            var hash = ix * 73856093 ^ iz * 19349663;
            hash ^= hash >> 13;
            hash *= 0x5bd1e995;
            hash ^= hash >> 15;
            return hash;
        }
    }

    private sealed class Tile
    {
        public float OriginX;
        public float OriginZ;
        public float[] Heights = null!;
    }
}
