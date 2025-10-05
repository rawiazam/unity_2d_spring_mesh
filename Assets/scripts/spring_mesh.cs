using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;
using UnityEngine.Profiling;
using UnityEngine.Rendering;


public class spring_mesh : MonoBehaviour
{
    private class Line
    {
        public GameObject[] points;
        private NativeArray<Vector2> positions;

        public LineRenderer renderer;

        public Line(GameObject[] points, NativeArray<Vector2> positions, LineRenderer renderer)
        {
            this.points = points;
            this.renderer = renderer;
            this.positions = positions;
        }

        public void Update()
        {
            Vector3[] vec3Positions = new Vector3[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                vec3Positions[i] = positions[i];
            }
            renderer.SetPositions(vec3Positions);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Spring
    {
        public int firstObjIndex;
        public int secondObjIndex;
        public float initialLength;
        public Spring(GameObject first, GameObject second, int firstTransformIndex, int secondTransformIndex)
        {
            this.firstObjIndex = firstTransformIndex;
            this.secondObjIndex = secondTransformIndex;
            this.initialLength = Vector2.Distance(first.transform.position, second.transform.position);
        }
    }

    private struct ShaderResult
    {
        public Vector2 firstVelocity;
        public Vector2 secondVelocity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShaderInput
    {
        public Vector2 position;
        public Vector2 velocity;
        // public Vector2 firstPoint;
        // public Vector2 firstInitialPosition;
        // public Vector2 firstVelocity;
        // public Vector2 secondPoint;
        // public Vector2 secondInitialPosition;
        // public Vector2 secondVelocity;
        // public float minDistance;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpringShaderInfo
    {
        public int firstPointIndex;
        public int secondPointIndex;
        public float initialDistance;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PersistentPointInfo
    {
        public Vector2 InitialPosition;
    }
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

    private GameObject meshOrigin;
    private Dictionary<GameObject, Vector3> initialPositions = new Dictionary<GameObject, Vector3>();
    private List<GameObject> points = new List<GameObject>();
    private List<mesh_point> mesh_points = new();
    private List<Line> lines = new List<Line>();
    private List<Spring> _springs = new List<Spring>();
    private NativeArray<Spring> springs;
    private List<Transform> pointTransforms = new();
    private NativeArray<Vector3> positions = new();
    private NativeArray<bool> staticIndecies;
    private List<bool> _staticIndecies = new();

    private TransformAccessArray transformAccessArray;
    private NativeArray<ShaderResult>[] nativeResults = new NativeArray<ShaderResult>[20];
    private uint currentNativeArrayIndex;


    NativeArray<ShaderInput> shaderInput;
    ComputeBuffer pointInfoBuffer;
    ComputeBuffer SpringInfoBuffer;
    NativeArray<SpringShaderInfo> springInfoBufferArray;
    ComputeBuffer persistentPointInfoBuffer;
    NativeArray<PersistentPointInfo> persistentPointInfoBufferArray;
    ComputeBuffer resultBuffer;
    ShaderResult[] results;
    private JobHandle updateHashMapJob = default;

    private dynamic_mesh dynamicMesh;

    void OnDestroy()
    {
        pointInfoBuffer.Release();
        resultBuffer.Release();
        SpringInfoBuffer.Release();
        persistentPointInfoBuffer.Release();
        positions.Dispose();
        springs.Dispose();
        pointVelocities.Dispose();
        shaderInput.Dispose();
        // nativeResults.Dispose();
    }
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        meshOrigin = new GameObject("mesh");
        meshOrigin.transform.position = new Vector2(0, 0);
        int widthOffset = (int)(width * density / 2.0);
        int heightOffset = (int)(height * density / 2.0);
        int currentIndex = 0;
        List<GameObject> columns = new List<GameObject>();
        for (int i = -heightOffset; i < heightOffset; i++)
        {
            for (int j = -widthOffset; j < widthOffset; j++)
            {
                Vector2 meshOffset = new Vector2(j / (float)density, i / (float)density);
                GameObject point = new GameObject($"meshPoint{points.Count}");
                point.transform.parent = meshOrigin.transform;
                point.transform.position = meshOrigin.transform.position + (Vector3)meshOffset;
                mesh_point pointComponent = point.AddComponent<mesh_point>();
                // point.AddComponent<SpriteRenderer>();
                // point.GetComponent<SpriteRenderer>().sprite = target_sprite;
                // point.GetComponent<SpriteRenderer>().sortingOrder = 2;
                // point.GetComponent<SpriteRenderer>().color = new Color(0, 115 / 255, 255f / 255, 0.8f);
                //point.transform.localScale = new Vector3(0.3f, 0.3f, 0.3f);

                if (j == -widthOffset || i == -heightOffset || j == widthOffset - 1 || i == heightOffset - 1)
                {
                    _staticIndecies.Add(true);
                    point.GetComponent<mesh_point>().isStatic = true;
                }
                else
                {
                    _staticIndecies.Add(false);
                }

                initialPositions[point] = point.transform.position;

                if (j > -widthOffset)
                {
                    _springs.Add(new Spring(point, points.Last(), points.Count(), points.Count() - 1));
                }
                if (i > -heightOffset)
                {
                    int previousOffset = (i - 1 + heightOffset) * widthOffset * 2 + j + widthOffset;
                    _springs.Add(new Spring(point, points[previousOffset], points.Count(), previousOffset));
                }
                pointComponent.pointIndex = points.Count();
                points.Add(point);
                pointTransforms.Add(point.transform);
                mesh_points.Add(pointComponent);
                currentIndex++;
            }
            // AddLine(points.GetRange(points.Count - widthOffset * 2, widthOffset * 2).ToArray());
        }
        for (int row = 1; row < heightOffset * 2; row++)
        {
            for (int col = 1; col < widthOffset * 2 - 1; col++)
            {
                int firstIndex = row * widthOffset * 2 + col;
                int secondIndex = (row - 1) * widthOffset * 2 + col - 1;
                int thirdIndex = (row - 1) * widthOffset * 2 + col + 1;
                _springs.Add(new Spring(points[firstIndex], points[secondIndex], firstIndex, secondIndex));
                _springs.Add(new Spring(points[firstIndex], points[thirdIndex], firstIndex, thirdIndex));
            }
        }
        // for (int i = 0; i < widthOffset * 2; i++)
        // {
        //     for (int j = 0; j < heightOffset * 2; j++)
        //     {
        //         columns.Add(points[i + j * widthOffset * 2]);
        //     }
        //     AddLine(columns.GetRange(columns.Count - heightOffset * 2, heightOffset * 2).ToArray());
        // }
        Debug.Log($"point amount: {points.Count}");
        Debug.Log($"spring amount: {_springs.Count}");

        springs = new NativeArray<Spring>(_springs.Count(), Allocator.Persistent);
        for (int i = 0; i < _springs.Count(); i++) springs[i] = _springs[i];
        pointInfoBuffer = new ComputeBuffer(springs.Length, Marshal.SizeOf(typeof(ShaderInput)));
        persistentPointInfoBuffer = new ComputeBuffer(points.Count, Marshal.SizeOf(typeof(PersistentPointInfo)));
        persistentPointInfoBufferArray = new NativeArray<PersistentPointInfo>(points.Count, Allocator.Persistent);
        SpringInfoBuffer = new ComputeBuffer(_springs.Count, Marshal.SizeOf(typeof(SpringShaderInfo)));
        springInfoBufferArray = new NativeArray<SpringShaderInfo>(_springs.Count, Allocator.Persistent);
        resultBuffer = new ComputeBuffer(springs.Length, Marshal.SizeOf(typeof(ShaderResult)));
        shaderInput = new NativeArray<ShaderInput>(points.Count(), Allocator.Persistent);
        positions = new NativeArray<Vector3>(points.Count(), Allocator.Persistent);
        pointVelocities = new NativeArray<Vector2>(points.Count(), Allocator.Persistent);
        spatialHash = new SpatialHash2D(points.Count, 1);
        staticIndecies = new NativeArray<bool>(points.Count, Allocator.Persistent);
        results = new ShaderResult[_springs.Count()];
        staticIndecies.CopyFrom(_staticIndecies.ToArray());
        for (int i = 0; i < nativeResults.Length; i++)
        {

            nativeResults[i] = new NativeArray<ShaderResult>(springs.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }
        // AddLine(points.ToArray(), positions);
        GameObject m = new GameObject("dynamic mesh");
        dynamicMesh = m.AddComponent<dynamic_mesh>();
        dynamicMesh.cols = width * density;
        dynamicMesh.rows = height * density;
        dynamicMesh.mat = meshMaterial;
        dynamicMesh.positions = positions;

        for (int i = 0; i < points.Count(); i++)
        {
            GameObject point = points[i];
            mesh_point pointComp = point.GetComponent<mesh_point>();
            pointComp.positions = positions;
            pointComp.velocities = pointVelocities;
            positions[i] = point.transform.position;

            PersistentPointInfo shaderInfo = new();
            shaderInfo.InitialPosition = point.transform.position;
            persistentPointInfoBufferArray[i] = shaderInfo;
        }

        for (int i = 0; i < springs.Length; i++)
        {
            SpringShaderInfo shaderInfo = new();
            Spring spring = springs[i];
            shaderInfo.firstPointIndex = spring.firstObjIndex;
            shaderInfo.secondPointIndex = spring.secondObjIndex;
            shaderInfo.initialDistance = spring.initialLength;
            springInfoBufferArray[i] = shaderInfo;
        }
        int kernel = springComputeShader.FindKernel("CSMain");
        SpringInfoBuffer.SetData(springInfoBufferArray);
        persistentPointInfoBuffer.SetData(persistentPointInfoBufferArray);
        springComputeShader.SetBuffer(kernel, "persistentPointInfo", persistentPointInfoBuffer);
        springComputeShader.SetBuffer(kernel, "springInfos", SpringInfoBuffer);
        transformAccessArray = new TransformAccessArray(pointTransforms.ToArray());
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
        // var spatialJob = new BuildHashJob
        // {
        //     Hash = spatialHash.AsParallelWriter(),
        //     Positions = positions
        // };

        JobHandle handle = job.Schedule(positions.Length, 254);//transformAccessArray);
        handle.Complete();
        Profiler.EndSample();
        // Profiler.BeginSample("spatial hash update");
        // JobHandle spatialHandle = spatialJob.Schedule(positions.Length, 64);
        // spatialHandle.Complete();
        // Profiler.EndSample();
    }

    // Update is called once per frame
    void LateUpdate()
    {
        // Profiler.BeginSample("fetch positions job");
        // var job = new FechPositionsJob
        // {
        //     positions = positions
        // };
        // JobHandle handle = job.Schedule(transformAccessArray);
        // handle.Complete();
        // Profiler.EndSample();

        Profiler.BeginSample("shaderinput struct");
        // for (int i = 0; i < springs.Length; i++)
        // {
        //     ShaderInput shader = shaderInput[i];
        //     Spring spring = springs[i];

        //     shader.firstPoint = positions[spring.firstObjIndex];
        //     shader.secondPoint = positions[spring.secondObjIndex];
        //     shader.firstVelocity = pointVelocities[spring.firstObjIndex];
        //     shader.secondVelocity = pointVelocities[spring.secondObjIndex];
        //     shaderInput[i] = shader;
        // }
        var inputJob = new BuildShaderInputJob
        {
            pointVelocities = pointVelocities,
            positions = positions,
            springs = springs,
            shaderInput = shaderInput

        };
        JobHandle inputHandle = inputJob.Schedule(shaderInput.Length, 64);
        inputHandle.Complete();
        Profiler.EndSample();

        Profiler.BeginSample("gpu input data copying");
        pointInfoBuffer.SetData(shaderInput);

        int kernel = springComputeShader.FindKernel("CSMain");

        springComputeShader.SetBuffer(kernel, "points", pointInfoBuffer);
        springComputeShader.SetBuffer(kernel, "results", resultBuffer);
        springComputeShader.SetInt("pairCount", springs.Length);
        springComputeShader.SetFloat("springConstant", springConstant);
        springComputeShader.SetFloat("damping", dampingForce);
        springComputeShader.SetFloat("returnForce", returnForce);
        springComputeShader.SetFloat("maxReturnDistance", maxReturnDistance);
        springComputeShader.SetFloat("minimumGate", minVelocityGate * 10);
        springComputeShader.SetFloat("deltaTime", Time.deltaTime);

        int threadsPerGroup = 256;
        int groups = Mathf.CeilToInt(springs.Length / (float)threadsPerGroup);
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
        // nativeResults[usedIndex].CopyFrom(results);
        Profiler.EndSample();

        Profiler.BeginSample("update velocities");
        var job = new UpdateVelocitiesJob
        {
            pointsVelocities = pointVelocities,
            results = nativeResults[usedIndex],
            springs = springs,
            DeltaTime = Time.deltaTime
        };
        JobHandle handle = job.Schedule(springs.Length, 64);
        handle.Complete();

        // for (int i = 0; i < springs.Length; i++)
        // {
        // Spring spring = springs[i];
        // ShaderResult result = results[i];
        // pointsVelocities[spring.firstObjIndex] += result.firstVelocity * Time.fixedDeltaTime;
        // pointsVelocities[spring.secondObjIndex] += result.secondVelocity * Time.fixedDeltaTime;
        // spring.firstPoint.AddVelocity(result.firstVelocity * Time.fixedDeltaTime);
        // spring.secondPoint.AddVelocity(result.secondVelocity * Time.fixedDeltaTime);
        // }
        Profiler.EndSample();
        UpdateHashMap();
    }

    [BurstCompile]
    private struct FechPositionsJob : IJobParallelForTransform
    {
        public NativeArray<Vector2> positions;
        public void Execute(int index, TransformAccess transform)
        {
            positions[index] = new Vector3(transform.position.x, transform.position.y, 0);
        }

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

            Vector2 newPos = objPosition + velocity * Mathf.Min(deltaTime, 0.025f);
            // transform.position = newPos;
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
        [ReadOnly] public NativeArray<Spring> springs;
        public NativeArray<ShaderInput> shaderInput;

        public void Execute(int index)
        {
            ShaderInput shader = shaderInput[index];
            // Spring spring = springs[index];

            shader.position = positions[index];
            shader.velocity = pointVelocities[index];

            // shader.firstPoint = positions[spring.firstObjIndex];
            // shader.secondPoint = positions[spring.secondObjIndex];
            // shader.firstVelocity = pointVelocities[spring.firstObjIndex];
            // shader.secondVelocity = pointVelocities[spring.secondObjIndex];
            // shader.minDistance = spring.initialLength;
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
        [ReadOnly]
        public NativeArray<Spring> springs;

        public float DeltaTime;

        public void Execute(int index)
        {
            var spring = springs[index];
            ShaderResult result = results[index];
            pointsVelocities[spring.firstObjIndex] += result.firstVelocity;
            pointsVelocities[spring.secondObjIndex] += result.secondVelocity;
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

    public List<Transform> GetPoints()
    {
        return pointTransforms;
    }

    public ref NativeArray<Vector2> GetVelocities()
    {
        return ref pointVelocities;
    }

    public ref NativeArray<Vector3> GetPositions()
    {
        return ref positions;
    }


    private void AddLine(GameObject[] points, NativeArray<Vector2> positions)
    {
        // Add a LineRenderer component
        GameObject renderer = new GameObject("linerenderer");
        renderer.transform.parent = gameObject.transform;
        LineRenderer lineRenderer = renderer.AddComponent<LineRenderer>();

        // Set the material
        lineRenderer.material = new Material(Resources.Load("test_sprite") as Material);//Shader.Find("Sprites/Default"));
        // lineRenderer.materials = new Material[] {Resources.Load("Assets/scripts/test.png") as Sprite};

        // Set the color
        lineRenderer.startColor = new Color(0, 59 / 255, 1, 0.6f);
        lineRenderer.endColor = new Color(0, 59 / 255, 1, 0.6f);

        // Set the width
        lineRenderer.startWidth = 0.03f;
        lineRenderer.endWidth = 0.03f;

        // Set the number of vertices
        lineRenderer.positionCount = points.Length;

        Line line = new Line(points, positions, lineRenderer);
        lines.Add(line);
    }

    public float GetConstant()
    {
        return springConstant;
    }
}
