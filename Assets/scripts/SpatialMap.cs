

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UI;


/// <summary>
/// Burst/job-safe 2D spatial hash.
/// Each cell key is a single int; values are ints (e.g., indices into your positions array).
/// </summary>
public struct SpatialHash2D : INativeDisposable
{
    public float cellSize;
    private NativeParallelMultiHashMap<long, int> bucket_a;
    private NativeParallelMultiHashMap<long, int> bucket_b;
    private bool toggle;
    private NativeParallelMultiHashMap<long, int> _write
    {
        get
        {
            return toggle ? bucket_b : bucket_a;
        }
    }
    private NativeParallelMultiHashMap<long, int> _read
    {
        get
        {
            return toggle ? bucket_a : bucket_b;
        }
    }

    /// <param name="initialCapacity">
    /// Estimated number of (cell, value) pairs you'll insert per frame. Set high to avoid reallocs.
    /// </param>
    public SpatialHash2D(int initialCapacity, float cellSize)
    {
        this.cellSize = cellSize;
        toggle = false;
        bucket_a = new NativeParallelMultiHashMap<long, int>(initialCapacity, Allocator.Persistent);
        bucket_b = new NativeParallelMultiHashMap<long, int>(initialCapacity, Allocator.Persistent);

    }

    /// <summary>Remove all entries. Capacity is unchanged.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearWrite()
    {
        _write.Clear();
    }

    /// <summary>Ensure the map can hold at least this many (cell,value) pairs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reserve(int capacity)
    {
        if (capacity > _write.Capacity)
        {
            bucket_a.Capacity = capacity;
            bucket_b.Capacity = capacity;
        }
    }

    /// <summary>Dispose immediately (main thread) — or schedule Dispose(jobHandle) for jobs.</summary>
    public void Dispose()
    {
        if (_write.IsCreated)
        {
            _write.Dispose();
            _read.Dispose();
        }
    }

    public struct DisposeJob : IJob
    {
        public NativeParallelMultiHashMap<long, int> first;
        public NativeParallelMultiHashMap<long, int> second;

        public void Execute()
        {
            first.Dispose();
            second.Dispose();
        }
    }

    public JobHandle Dispose(JobHandle inputDeps)
    {
        if (_write.IsCreated)
        {
            var job = new DisposeJob
            {
                first = _write,
                second = _read
            };
            return job.Schedule(inputDeps);
        }

        return inputDeps;
    }

    /// <summary>Return the integer grid coordinate for a world position.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int2 GridCoord(float2 p)
    {
        // Floor division into grid cells; handles negatives correctly.
        return (int2)math.floor(p / cellSize);
    }

    /// <summary>Stable int key for a world position (hash of the cell coords).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long KeyForPosition(float2 p)
    {
        return KeyForCell(GridCoord(p));
    }

    /// <summary>Stable int key for a given grid cell.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long KeyForCell(int2 cell)
    {
        // Use Unity.Mathematics hash to map int2 -> uint, then reinterpret to int.
        // Keys are just identifiers; the hash map applies its own hashing internally too.
        return unchecked(((long)cell.x << 32) | (uint)cell.y);
    }

    /// <summary>Add a single value (e.g., object index) to the bucket for this position.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(float2 position, int value)
    {
        _write.Add(KeyForPosition(position), value);
    }

    /// <summary>Parallel writer wrapper for inserts inside IJobParallelFor, etc.</summary>
    public struct ParallelWriter
    {
        internal NativeParallelMultiHashMap<long, int>.ParallelWriter Writer;
        internal float CellSize;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(float2 position, int value)
        {
            int2 cell = (int2)math.floor(position / CellSize);
            long key = SpatialHash2D.KeyForCell(cell);
            Writer.Add(key, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int KeyForCell(int2 cell) => unchecked((int)math.hash(cell));
    }

    /// <summary>Get a thread-safe parallel writer for use in jobs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParallelWriter AsParallelWriter()
    {
        return new ParallelWriter
        {
            Writer = _write.AsParallelWriter(),
            CellSize = cellSize
        };
    }

    /// <summary>
    /// Query all values whose positions lie within radius of center.
    /// Writes results into caller-provided NativeList&lt;int&gt; (not cleared here).
    ///
    /// - positions: NativeArray of float2 indexed by the stored values/IDs.
    /// - results:   Append-only result sink (TempJob/Allocator.Persistent recommended).
    ///
    /// This can be called from a Burst-compiled job.
    /// </summary>
    public List<int> Query(float2 center, float radius,
                          [ReadOnly] NativeArray<Vector3> positions)
    {
        List<int> results = new();
        float inv = 1f / cellSize;
        int minX = (int)math.floor((center.x - radius) * inv);
        int maxX = (int)math.floor((center.x + radius) * inv);
        int minY = (int)math.floor((center.y - radius) * inv);
        int maxY = (int)math.floor((center.y + radius) * inv);

        float r2 = radius * radius;

        for (int cy = minY; cy <= maxY; cy++)
        {
            for (int cx = minX; cx <= maxX; cx++)
            {
                // World-space AABB of this cell
                float cellMinX = cx * cellSize;
                float cellMaxX = (cx + 1) * cellSize;
                float cellMinY = cy * cellSize;
                float cellMaxY = (cy + 1) * cellSize;

                // 1) Quick reject: cell entirely outside circle?
                // Distance from center to AABB (squared)
                float dx = 0f;
                if (center.x < cellMinX) dx = cellMinX - center.x;
                else if (center.x > cellMaxX) dx = center.x - cellMaxX;

                float dy = 0f;
                if (center.y < cellMinY) dy = cellMinY - center.y;
                else if (center.y > cellMaxY) dy = center.y - cellMaxY;

                float nearestDist2 = dx * dx + dy * dy;
                if (nearestDist2 > r2)
                    continue; // no overlap at all

                // 2) Check if the entire cell is inside the circle:
                // all four corners must be within radius
                float2 c = center;
                bool fullyInside =
                    math.lengthsq(new float2(cellMinX, cellMinY) - c) <= r2 &&
                    math.lengthsq(new float2(cellMinX, cellMaxY) - c) <= r2 &&
                    math.lengthsq(new float2(cellMaxX, cellMinY) - c) <= r2 &&
                    math.lengthsq(new float2(cellMaxX, cellMaxY) - c) <= r2;

                long key = KeyForCell(new int2(cx, cy));

                if (_read.TryGetFirstValue(key, out int value, out var it))
                {
                    if (fullyInside)
                    {
                        // Interior cell: take everything without per-point checks
                        do
                        {
                            results.Add(value);
                        }
                        while (_read.TryGetNextValue(out value, ref it));
                    }
                    else
                    {
                        // Boundary cell: filter by circle
                        do
                        {
                            float2 p = new float2(positions[value].x, positions[value].y);
                            float2 d = p - center;
                            if (math.lengthsq(d) <= r2)
                                results.Add(value);
                        }
                        while (_read.TryGetNextValue(out value, ref it));
                    }
                }
            }
        }

        return results;
    }
    /// <summary>Returns the number of (cell,value) pairs currently stored.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Count() => _read.Count();

    /// <summary>True if the backing map exists and currently has no entries.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEmpty() => _read.IsEmpty;

    /// <summary>Useful when you want to inspect or iterate externally.</summary>
    public NativeParallelMultiHashMap<long, int> AsMap() => _read;

    public void Toggle()
    {
        toggle = !toggle;
    }
}

[BurstCompile]
public struct BuildHashJob : IJobParallelFor
{
    public SpatialHash2D.ParallelWriter Hash;
    [ReadOnly] public NativeArray<Vector3> Positions;

    public void Execute(int index)
    {
        Vector2 position = Positions[index];
        Hash.Add(position, index);
    }
}
