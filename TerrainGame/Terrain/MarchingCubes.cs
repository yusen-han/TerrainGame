using System;
using System.Collections.Generic;
using System.Numerics;

namespace TerrainGame.Terrain;

/// <summary>
/// Marching Cubes over a scalar field. Corner/normal conventions follow the
/// classic Paul Bourke ordering. Cube faces are polygonised by connecting the
/// edge intersections around each face into loops, which removes the need for
/// the 256-entry triangle table while still producing the standard topology.
/// </summary>
public static class MarchingCubes
{
    private static readonly Vector3[] CornerOffsets =
    [
        new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0),
        new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1),
    ];

    private static readonly int[] EdgeCorners =
    [
        0, 1, 1, 2, 2, 3, 3, 0,
        4, 5, 5, 6, 6, 7, 7, 4,
        0, 4, 1, 5, 2, 6, 3, 7,
    ];

    // Start sample offset (di, dj, dk) and axis (0 = X, 1 = Y, 2 = Z) per edge.
    private static readonly int[,] EdgeStart =
    {
        { 0, 0, 0, 0 }, { 1, 0, 0, 1 }, { 0, 1, 0, 0 }, { 0, 0, 0, 1 },
        { 0, 0, 1, 0 }, { 1, 0, 1, 1 }, { 0, 1, 1, 0 }, { 0, 0, 1, 1 },
        { 0, 0, 0, 2 }, { 1, 0, 0, 2 }, { 1, 1, 0, 2 }, { 0, 1, 0, 2 },
    };

    private static readonly int[,] FaceCorners =
    {
        { 0, 1, 2, 3 }, { 4, 5, 6, 7 },
        { 0, 1, 5, 4 }, { 3, 2, 6, 7 },
        { 0, 3, 7, 4 }, { 1, 2, 6, 5 },
    };

    private static readonly int[,] FaceEdges =
    {
        { 0, 1, 2, 3 }, { 4, 5, 6, 7 },
        { 0, 9, 4, 8 }, { 2, 10, 6, 11 },
        { 3, 11, 7, 8 }, { 1, 10, 5, 9 },
    };

    /// <summary>
    /// Builds a triangle soup for the iso-surface <c>density == 0</c>.
    /// Positive density is treated as solid (inside).
    /// </summary>
    public static TerrainVertex[] Generate(float[,,] density, Vector3 origin, float voxelSize)
    {
        var sampleCount = density.GetLength(0);
        var cellCount = sampleCount - 1;
        if (density.GetLength(1) != sampleCount || density.GetLength(2) != sampleCount)
            throw new ArgumentException("Density grid must be cubic.", nameof(density));

        var positions = new List<Vector3>(1024);
        var vertices = new List<TerrainVertex>(3072);
        var edgeCache = new Dictionary<long, int>(4096);

        Span<float> cornerDensity = stackalloc float[8];
        Span<int> edgePoint = stackalloc int[12];
        Span<int> faceEdges = stackalloc int[4];
        Span<bool> inside = stackalloc bool[4];

        for (var k = 0; k < cellCount; k++)
        {
            for (var j = 0; j < cellCount; j++)
            {
                for (var i = 0; i < cellCount; i++)
                {
                    var mask = 0;
                    for (var c = 0; c < 8; c++)
                    {
                        var value = density[i + (int)CornerOffsets[c].X,
                                             j + (int)CornerOffsets[c].Y,
                                             k + (int)CornerOffsets[c].Z];
                        cornerDensity[c] = value;
                        if (value > 0f)
                            mask |= 1 << c;
                    }

                    if (mask == 0 || mask == 255)
                        continue;

                    // Coarse gradient at the cell, used only to orient triangles.
                    var gradient = new Vector3(
                        density[i + 1, j, k] - density[i, j, k],
                        density[i, j + 1, k] - density[i, j, k],
                        density[i, j, k + 1] - density[i, j, k]);

                    for (var e = 0; e < 12; e++)
                        edgePoint[e] = -1;

                    for (var e = 0; e < 12; e++)
                    {
                        var a = EdgeCorners[e * 2];
                        var b = EdgeCorners[e * 2 + 1];
                        if (((mask >> a) & 1) == ((mask >> b) & 1))
                            continue;

                        var axis = EdgeStart[e, 3];
                        var startX = i + EdgeStart[e, 0];
                        var startY = j + EdgeStart[e, 1];
                        var startZ = k + EdgeStart[e, 2];

                        var key = ((long)startX * sampleCount + startY) * sampleCount * 3
                                  + startZ * 3 + axis;
                        if (!edgeCache.TryGetValue(key, out var index))
                        {
                            var da = cornerDensity[a];
                            var db = cornerDensity[b];
                            var t = da / (da - db);

                            var local = new Vector3(i, j, k)
                                        + CornerOffsets[a]
                                        + (CornerOffsets[b] - CornerOffsets[a]) * t;

                            index = positions.Count;
                            positions.Add(origin + local * voxelSize);
                            edgeCache.Add(key, index);
                        }

                        edgePoint[e] = index;
                    }

                    var segments = new List<(int A, int B)>(6);

                    for (var f = 0; f < 6; f++)
                    {
                        for (var c = 0; c < 4; c++)
                        {
                            faceEdges[c] = FaceEdges[f, c];
                            inside[c] = ((mask >> FaceCorners[f, c]) & 1) != 0;
                        }

                        var crossingCount = 0;
                        for (var c = 0; c < 4; c++)
                        {
                            if (inside[c] != inside[(c + 1) % 4])
                                crossingCount++;
                        }

                        if (crossingCount == 2)
                        {
                            var first = -1;
                            var second = -1;
                            for (var c = 0; c < 4; c++)
                            {
                                if (edgePoint[faceEdges[c]] < 0)
                                    continue;
                                if (first < 0) first = edgePoint[faceEdges[c]];
                                else second = edgePoint[faceEdges[c]];
                            }

                            if (first >= 0 && second >= 0)
                                segments.Add((first, second));
                        }
                        else if (crossingCount == 4)
                        {
                            for (var c = 0; c < 4; c++)
                            {
                                if (!inside[c])
                                    continue;

                                var e1 = faceEdges[(c + 3) % 4];
                                var e2 = faceEdges[c];
                                if (edgePoint[e1] >= 0 && edgePoint[e2] >= 0)
                                    segments.Add((edgePoint[e1], edgePoint[e2]));
                            }
                        }
                    }

                    if (segments.Count >= 3)
                        TraceLoops(segments, positions, vertices, gradient);
                }
            }
        }

        return vertices.ToArray();
    }

    private static void TraceLoops(
        List<(int A, int B)> segments,
        List<Vector3> positions,
        List<TerrainVertex> vertices,
        Vector3 gradient)
    {
        var adjacency = new Dictionary<int, List<int>>(12);
        foreach (var (a, b) in segments)
        {
            if (!adjacency.TryGetValue(a, out var listA))
                adjacency[a] = listA = new List<int>(2);
            if (!adjacency.TryGetValue(b, out var listB))
                adjacency[b] = listB = new List<int>(2);
            listA.Add(b);
            listB.Add(a);
        }

        var visited = new HashSet<int>();
        var loop = new List<int>(8);

        foreach (var start in adjacency.Keys)
        {
            if (visited.Contains(start))
                continue;

            loop.Clear();
            var previous = -1;
            var current = start;

            while (true)
            {
                loop.Add(current);
                visited.Add(current);

                var neighbours = adjacency[current];
                int next;
                if (neighbours.Count > 1 && neighbours[0] == previous)
                    next = neighbours[1];
                else if (neighbours.Count > 1 && neighbours[1] == previous)
                    next = neighbours[0];
                else
                    next = neighbours.Count > 0 ? neighbours[0] : -1;

                if (next < 0 || next == start || visited.Contains(next))
                    break;

                previous = current;
                current = next;
            }

            if (loop.Count >= 3)
                EmitLoop(loop, positions, vertices, gradient);
        }
    }

    private static void EmitLoop(List<int> loop, List<Vector3> positions, List<TerrainVertex> vertices, Vector3 gradient)
    {
        if (loop.Count < 3)
            return;

        var p0 = positions[loop[0]];
        for (var i = 1; i < loop.Count - 1; i++)
        {
            var p1 = positions[loop[i]];
            var p2 = positions[loop[i + 1]];
            AddTriangle(p0, p1, p2, gradient, vertices);
        }
    }

    private static void AddTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 gradient, List<TerrainVertex> vertices)
    {
        var normal = Vector3.Cross(b - a, c - a);
        if (normal.LengthSquared() < 1e-10f)
            return;

        normal = Vector3.Normalize(normal);

        // Solid is where density > 0, so the outward normal points against the gradient.
        if (Vector3.Dot(normal, gradient) > 0f)
            normal = -normal;

        vertices.Add(new TerrainVertex(a, normal));
        vertices.Add(new TerrainVertex(b, normal));
        vertices.Add(new TerrainVertex(c, normal));
    }
}
