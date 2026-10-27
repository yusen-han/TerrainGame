using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TerrainGame.Terrain;

/// <summary>
/// Streams terrain chunks around the camera on background workers and exposes
/// finished meshes plus keys that fell out of range. All public methods are for
/// the render thread; workers only read requests and push results.
/// </summary>
public sealed class ChunkStreamer : IDisposable
{
    public readonly record struct CompletedChunk(long Key, TerrainVertex[] Vertices);

    private readonly TerrainGenerator _generator;
    private readonly int _radius;
    private readonly Channel<long> _requests = Channel.CreateUnbounded<long>();
    private readonly Queue<CompletedChunk> _completed = new();
    private readonly object _completedLock = new();
    private readonly HashSet<long> _desired = new();
    private readonly HashSet<long> _known = new();
    private readonly List<long> _removals = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task[] _workers;

    public ChunkStreamer(int seed, int radius = 4, int workerCount = 3)
    {
        _generator = new TerrainGenerator(seed);
        _radius = radius;
        _workers = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
            _workers[i] = Task.Run(WorkerAsync);
    }

    public int Radius => _radius;

    /// <summary>Requests chunks around <paramref name="position"/> and queues distant ones for removal.</summary>
    public void Update(Vector3 position)
    {
        var chunkWorldSize = TerrainGenerator.ChunkSize * TerrainGenerator.VoxelSize;
        var centerX = (int)MathF.Floor(position.X / chunkWorldSize);
        var centerZ = (int)MathF.Floor(position.Z / chunkWorldSize);

        _desired.Clear();
        for (var dz = -_radius; dz <= _radius; dz++)
        for (var dx = -_radius; dx <= _radius; dx++)
            _desired.Add(TerrainGenerator.ChunkKey(centerX + dx, centerZ + dz));

        foreach (var key in _desired)
        {
            if (_known.Add(key))
                _requests.Writer.TryWrite(key);
        }

        _removals.Clear();
        foreach (var key in _known)
        {
            if (_desired.Contains(key))
                continue;

            _removals.Add(key);
        }

        foreach (var key in _removals)
            _known.Remove(key);
    }

    /// <summary>Takes generated chunks that are still wanted.</summary>
    public void DrainReady(List<CompletedChunk> output)
    {
        lock (_completedLock)
        {
            while (_completed.Count > 0)
            {
                var chunk = _completed.Dequeue();
                if (_desired.Contains(chunk.Key))
                    output.Add(chunk);
            }
        }
    }

    /// <summary>Takes chunk keys that should be released by the renderer.</summary>
    public void DrainRemovals(List<long> output)
    {
        output.AddRange(_removals);
        _removals.Clear();
    }

    private async Task WorkerAsync()
    {
        try
        {
            await foreach (var key in _requests.Reader.ReadAllAsync(_cts.Token))
            {
                var chunkX = (int)(key >> 32);
                var chunkZ = (int)(key & 0xFFFFFFFF);
                var vertices = _generator.GenerateChunk(chunkX, chunkZ);

                lock (_completedLock)
                    _completed.Enqueue(new CompletedChunk(key, vertices));
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _requests.Writer.TryComplete();
        try
        {
            Task.WaitAll(_workers, TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Workers were cancelled.
        }

        _cts.Dispose();
    }
}
