using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
using UnityEngine.Profiling;
using UnityEngine.Rendering;


public class SpringMesh : MonoBehaviour
{

    [StructLayout(LayoutKind.Sequential)]
    private struct Spring
    {
        public int firstObjIndex;
        public int secondObjIndex;
        public float initialLength;
        public Spring(Vector2 firstPos, Vector2 secondPos, int firstTransformIndex, int secondTransformIndex)
        {
            this.firstObjIndex = firstTransformIndex;
            this.secondObjIndex = secondTransformIndex;
            this.initialLength = Vector2.Distance(secondPos, firstPos);
        }
    }

    private struct ShaderResult
    {
        public Vector2 firstVelocity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShaderInput
    {
        public Vector2 position;
        public Vector2 velocity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpringShaderInfo
    {
        public int firstPointIndex;
        public int secondPointIndex;
        public float initialDistance;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct PersistentPointInfo
    {
        public Vector2 InitialPosition;
        public fixed int springIndecies[8];

        public PersistentPointInfo(Vector2 initialPosition)
        {
            this.InitialPosition = initialPosition;
        }

        public void Init()
        {
            for (int i = 0; i < 8; i++)
            {
                springIndecies[i] = -1;
            }
        }

        public void AddSpring(int index)
        {
            int currentIndex = 0;
            while (springIndecies[currentIndex] != -1)
            {
                currentIndex++;
                if (currentIndex == 8) throw new Exception($"tried adding more springs than length - {springIndecies[7]}");
            }
            springIndecies[currentIndex] = index;
        }
    }
    // ----- Inspector / config -----
    public int density;
    public int width;
    public int height;

    public float springConstant;
    public float dampingForce;
    public float returnForce;
    public float maxReturnDistance;
    public ComputeShader springComputeShader;

    public NativeArray<Vector2> pointVelocities;

    public Material meshMaterial;
    public float minVelocityGate;
    public SpatialHash2D spatialHash;

    // ----- Internals -----
    private GameObject meshOrigin;
    private readonly List<Spring> _springs = new List<Spring>();
    private NativeArray<Spring> springs;

    private readonly List<Vector2> points = new List<Vector2>();
    private NativeArray<Vector3> positions;

    private NativeArray<bool> staticIndecies;
    private readonly List<bool> _staticIndecies = new List<bool>();

    private NativeArray<ShaderResult>[] nativeResults = new NativeArray<ShaderResult>[20];
    private uint currentNativeArrayIndex;

    private NativeArray<ShaderInput> shaderInput;
    private ComputeBuffer pointInfoBuffer;
    private ComputeBuffer SpringInfoBuffer;
    private NativeArray<SpringShaderInfo> springInfoBufferArray;
    private ComputeBuffer persistentPointInfoBuffer;
    private NativeArray<PersistentPointInfo> persistentPointInfoBufferArray;
    private ComputeBuffer resultBuffer;

    private JobHandle updateHashMapJob = default;

    private dynamic_mesh dynamicMesh;

    private const int ThreadGroupSize = 256;

    void OnDestroy()
    {
        try { pointInfoBuffer?.Release(); } catch { }
        try { resultBuffer?.Release(); } catch { }
        try { SpringInfoBuffer?.Release(); } catch { }
        try { persistentPointInfoBuffer?.Release(); } catch { }

        if (positions.IsCreated) positions.Dispose();
        if (springs.IsCreated) springs.Dispose();
        if (pointVelocities.IsCreated) pointVelocities.Dispose();
        if (shaderInput.IsCreated) shaderInput.Dispose();
        if (staticIndecies.IsCreated) staticIndecies.Dispose();
        if (springInfoBufferArray.IsCreated) springInfoBufferArray.Dispose();
        if (persistentPointInfoBufferArray.IsCreated) persistentPointInfoBufferArray.Dispose();

        if (nativeResults != null)
        {
            for (int i = 0; i < nativeResults.Length; i++)
                if (nativeResults[i].IsCreated) nativeResults[i].Dispose();
        }
    }

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Awake()
    {
        meshOrigin = new GameObject("mesh");
        meshOrigin.transform.position = new Vector2(0, 0);
        int widthOffset = (int)(width * density / 2.0);
        int heightOffset = (int)(height * density / 2.0);
        Vector2 currentPosition = (Vector2)gameObject.transform.position;
        for (int i = -heightOffset; i < heightOffset; i++)
        {
            for (int j = -widthOffset; j < widthOffset; j++)
            {
                Vector2 meshOffset = new Vector2(j / (float)density, i / (float)density) + currentPosition;

                if (j == -widthOffset || i == -heightOffset || j == widthOffset - 1 || i == heightOffset - 1)
                {
                    _staticIndecies.Add(true);
                }
                else
                {
                    _staticIndecies.Add(false);
                }

                if (j > -widthOffset)
                {
                    _springs.Add(new Spring(meshOffset, points.Last(), points.Count(), points.Count() - 1));
                }
                if (i > -heightOffset)
                {
                    int previousOffset = (i - 1 + heightOffset) * widthOffset * 2 + j + widthOffset;
                    _springs.Add(new Spring(meshOffset, points[previousOffset], points.Count(), previousOffset));
                }

                points.Add(meshOffset);
            }
        }
        for (int row = 1; row < heightOffset * 2; row++)
        {
            for (int col = 1; col < widthOffset * 2 - 1; col++)
            {
                int currPoint = row * widthOffset * 2 + col;
                int upLeft = (row - 1) * widthOffset * 2 + col - 1;
                int upRight = (row - 1) * widthOffset * 2 + col + 1;
                _springs.Add(new Spring(points[currPoint], points[upLeft], currPoint, upLeft));
                _springs.Add(new Spring(points[currPoint], points[upRight], currPoint, upRight));
            }
        }
        Debug.Log($"point amount: {points.Count}");
        Debug.Log($"spring amount: {_springs.Count}");

        // Allocate persistent native data
        springs = new NativeArray<Spring>(_springs.Count, Allocator.Persistent);
        springs.CopyFrom(_springs.ToArray());

        shaderInput = new NativeArray<ShaderInput>(points.Count, Allocator.Persistent);
        positions = new NativeArray<Vector3>(points.Count, Allocator.Persistent);
        pointVelocities = new NativeArray<Vector2>(points.Count, Allocator.Persistent);
        staticIndecies = new NativeArray<bool>(points.Count, Allocator.Persistent);
        staticIndecies.CopyFrom(_staticIndecies.ToArray());
        spatialHash = new SpatialHash2D(points.Count, 1);

        // Buffers
        pointInfoBuffer = new ComputeBuffer(points.Count, Marshal.SizeOf(typeof(ShaderInput)));
        persistentPointInfoBuffer = new ComputeBuffer(points.Count, Marshal.SizeOf(typeof(PersistentPointInfo)));
        SpringInfoBuffer = new ComputeBuffer(_springs.Count, Marshal.SizeOf(typeof(SpringShaderInfo)));
        resultBuffer = new ComputeBuffer(points.Count, Marshal.SizeOf(typeof(ShaderResult)));
        springInfoBufferArray = new NativeArray<SpringShaderInfo>(_springs.Count, Allocator.Persistent);
        persistentPointInfoBufferArray = new NativeArray<PersistentPointInfo>(points.Count, Allocator.Persistent);

        for (int i = 0; i < nativeResults.Length; i++)
            nativeResults[i] = new NativeArray<ShaderResult>(points.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

        GameObject m = new GameObject("dynamic mesh");
        dynamicMesh = m.AddComponent<dynamic_mesh>();
        dynamicMesh.cols = widthOffset * 2;
        dynamicMesh.rows = heightOffset * 2;
        dynamicMesh.mat = meshMaterial;
        dynamicMesh.positions = positions;

        for (int i = 0; i < points.Count(); i++)
        {
            Vector2 point = points[i];
            positions[i] = point;

            PersistentPointInfo shaderInfo = new();
            shaderInfo.Init();
            shaderInfo.InitialPosition = point;
            persistentPointInfoBufferArray[i] = shaderInfo;
        }

        for (int i = 0; i < springs.Length; i++)
        {
            Spring spring = springs[i];
            SpringShaderInfo springShaderInfo = new SpringShaderInfo
            {
                firstPointIndex = spring.firstObjIndex,
                secondPointIndex = spring.secondObjIndex,
                initialDistance = spring.initialLength
            };
            springInfoBufferArray[i] = springShaderInfo;

            PersistentPointInfo p1 = persistentPointInfoBufferArray[spring.firstObjIndex];
            p1.AddSpring(i);
            persistentPointInfoBufferArray[spring.firstObjIndex] = p1;

            PersistentPointInfo p2 = persistentPointInfoBufferArray[spring.secondObjIndex];
            p2.AddSpring(i);
            persistentPointInfoBufferArray[spring.secondObjIndex] = p2;
        }

        // Setting up buffer vars
        int kernel = springComputeShader.FindKernel("CSMain");
        SpringInfoBuffer.SetData(springInfoBufferArray);
        persistentPointInfoBuffer.SetData(persistentPointInfoBufferArray);
        springComputeShader.SetBuffer(kernel, "persistentPointInfo", persistentPointInfoBuffer);
        springComputeShader.SetBuffer(kernel, "springInfos", SpringInfoBuffer);
        springComputeShader.SetBuffer(kernel, "points", pointInfoBuffer);
        springComputeShader.SetBuffer(kernel, "results", resultBuffer);
        springComputeShader.SetInt("pointCount", points.Count);
    }


    void Update()
    {
        Profiler.BeginSample("update positions");
        var job = new UpdatePointPositions
        {
            positions = positions,
            pointVelocities = pointVelocities,
            deltaTime = Time.deltaTime,
            fixedDeltaTime = Time.deltaTime,
            staticIndecies = staticIndecies,
        };

        JobHandle handle = job.Schedule(positions.Length, 254);
        handle.Complete();
        Profiler.EndSample();
    }

    // Update is called once per frame
    void LateUpdate()
    {
        Profiler.BeginSample("shaderinput struct saturation");
        var inputJob = new BuildShaderInputJob
        {
            pointVelocities = pointVelocities,
            positions = positions,
            shaderInput = shaderInput

        };
        JobHandle inputHandle = inputJob.Schedule(shaderInput.Length, 64);
        inputHandle.Complete();
        Profiler.EndSample();

        Profiler.BeginSample("gpu input data copying");
        pointInfoBuffer.SetData(shaderInput);

        int kernel = springComputeShader.FindKernel("CSMain");

        springComputeShader.SetFloat("springConstant", springConstant);
        springComputeShader.SetFloat("damping", dampingForce);
        springComputeShader.SetFloat("returnForce", returnForce);
        springComputeShader.SetFloat("maxReturnDistance", maxReturnDistance);
        springComputeShader.SetFloat("minimumGate", minVelocityGate * 10);
        springComputeShader.SetFloat("deltaTime", Mathf.Min(Time.deltaTime, 0.05f));

        int groups = Mathf.CeilToInt(points.Count / ThreadGroupSize);
        Profiler.EndSample();
        Profiler.BeginSample("dispatch");

        springComputeShader.Dispatch(kernel, groups, 1, 1);

        Profiler.EndSample();
        Profiler.BeginSample("line update");
        // foreach (Line line in lines)
        // {
        //     line.Update();
        // }
        Profiler.EndSample();

        // resultBuffer.GetData(results, 0, 0, springs.Length);
        Profiler.BeginSample("request data");
        var req = AsyncGPUReadback.RequestIntoNativeArray(ref nativeResults[currentNativeArrayIndex], resultBuffer);
        uint usedIndex = currentNativeArrayIndex;
        currentNativeArrayIndex++;
        if (currentNativeArrayIndex == nativeResults.Length) currentNativeArrayIndex = 0;
        Profiler.EndSample();
        Profiler.BeginSample("result get");
        dynamicMesh.UpdateMesh();
        req.WaitForCompletion();
        // debug loop
        // NativeArray<ShaderResult> res = nativeResults[usedIndex];
        // List<Vector2> vels = new();
        // foreach (ShaderResult r in res) vels.Add(r.firstVelocity);
        // Debug.Log($"[{string.Join(", ", vels)}]");
        // Debug.Log(res.Length);
        // Debug.Log(pointVelocities.Length);
        // nativeResults[usedIndex].CopyFrom(results);
        Profiler.EndSample();

        Profiler.BeginSample("update velocities");
        var job = new UpdateVelocitiesJob
        {
            pointsVelocities = pointVelocities,
            results = nativeResults[usedIndex],
        };
        JobHandle handle = job.Schedule(points.Count, 256);
        handle.Complete();

        Profiler.EndSample();
        // UpdateHashMap();
    }

    [BurstCompile]
    private struct UpdatePointPositions : IJobParallelFor
    {
        [NativeDisableContainerSafetyRestriction]
        public NativeArray<Vector3> positions;
        public NativeArray<Vector2> pointVelocities;
        public NativeArray<bool> staticIndecies;
        [ReadOnly] public float deltaTime;
        [ReadOnly] public float fixedDeltaTime;

        public void Execute(int index)
        {
            if (staticIndecies[index] == true)
            {
                pointVelocities[index] = Vector2.zero;
                return;
            }
            Vector2 objPosition = positions[index];
            Vector2 velocity = pointVelocities[index];
            velocity = Vector2.ClampMagnitude(velocity, 16);

            Vector2 newPos = objPosition + velocity * math.min(deltaTime, 0.025f);
            if (velocity.magnitude < 0.003f)
            {
                velocity = Vector2.zero;
            }
            velocity *= 0.98f;
            pointVelocities[index] = velocity;
            positions[index] = newPos;
        }
    }

    [BurstCompile]
    private struct BuildShaderInputJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction]
        [ReadOnly] public NativeArray<Vector2> pointVelocities;
        [NativeDisableParallelForRestriction]
        [ReadOnly] public NativeArray<Vector3> positions;
        public NativeArray<ShaderInput> shaderInput;

        public void Execute(int index)
        {
            ShaderInput shader = shaderInput[index];

            shader.position = positions[index];
            shader.velocity = pointVelocities[index];

            shaderInput[index] = shader;
        }
    }

    [BurstCompile]
    private struct UpdateVelocitiesJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction]
        public NativeArray<Vector2> pointsVelocities;
        [ReadOnly]
        public NativeArray<ShaderResult> results;

        public void Execute(int index)
        {
            ShaderResult result = results[index];
            pointsVelocities[index] += result.firstVelocity;
        }
    }

    private void UpdateHashMap()
    {
        Profiler.BeginSample("update spatial hash");
        updateHashMapJob.Complete();
        spatialHash.Toggle();
        spatialHash.ClearWrite();
        var spatialJob = new BuildHashJob
        {
            Hash = spatialHash.AsParallelWriter(),
            Positions = positions
        };
        updateHashMapJob = spatialJob.Schedule(positions.Length, 64, updateHashMapJob);
        Profiler.EndSample();
    }

    public ref NativeArray<Vector2> GetVelocities()
    {
        return ref pointVelocities;
    }

    public ref NativeArray<Vector3> GetPositions()
    {
        return ref positions;
    }


    public ref ComputeShader GetShader()
    {
        return ref springComputeShader;
    }

}
