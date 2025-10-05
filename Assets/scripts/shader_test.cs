// File: QuadDensityRenderer.cs
using UnityEngine;
using Unity.Collections;

[RequireComponent(typeof(MeshRenderer), typeof(MeshFilter))]
public class QuadDensityRenderer : MonoBehaviour
{
    [Header("Inputs")]
    public SpringMesh positionsProvider; // your script that exposes getPositions()

    [Header("Compute")]
    public ComputeShader densityCS;
    [Tooltip("Internal resolution of the density texture.")]
    public int width = 1024;
    public int height = 1024;
    public int splatRadius = 1;
    public float contribution = 1f;
    public bool enableBlur = true;

    [Header("World Bounds (points mapped into this rect)")]
    public bool autoBoundsEveryFrame = true;
    public Vector2 worldMin = new Vector2(-5, -5);
    public Vector2 worldMax = new Vector2(5, 5);

    [Header("Look")]
    [Tooltip("Higher = need more points to hit white. Start small (8-32).")]
    public float exposureMax = 32f;

    // Internal
    private ComputeBuffer pointsBuffer;
    private RenderTexture densityRT, tempRT;
    private int kClear, kAcc, kBlurH, kBlurV;

    // Cached IDs
    static readonly int PointsID = Shader.PropertyToID("_Points");
    static readonly int PointCountID = Shader.PropertyToID("_PointCount");
    static readonly int WidthID = Shader.PropertyToID("_Width");
    static readonly int HeightID = Shader.PropertyToID("_Height");
    static readonly int SplatRadiusID = Shader.PropertyToID("_SplatRadius");
    static readonly int ScaleID = Shader.PropertyToID("_Scale");
    static readonly int WorldMinID = Shader.PropertyToID("_WorldMin");
    static readonly int WorldMaxID = Shader.PropertyToID("_WorldMax");

    MeshRenderer quadRenderer;
    Material runtimeMat; // instance so we don't mutate shared asset

    void OnEnable()
    {
        quadRenderer = GetComponent<MeshRenderer>();
        runtimeMat = quadRenderer.material; // instanced copy
        if (runtimeMat.shader.name != "Custom/QuadDensity")
            Debug.LogWarning("Material is not using Custom/QuadDensity. Assign it to the Quad.");

        kClear = densityCS.FindKernel("Clear");
        kAcc = densityCS.FindKernel("AccumulatePoints");
        kBlurH = densityCS.FindKernel("BlurH");
        kBlurV = densityCS.FindKernel("BlurV");

        AllocateAll(1); // safe minimum
        BindStatics();
    }

    void AllocateAll(int pointCount)
    {
        // Points buffer
        pointsBuffer?.Release();
        pointsBuffer = new ComputeBuffer(Mathf.Max(pointCount, 1), sizeof(float) * 2, ComputeBufferType.Structured);

        // Density RTs (R32_UInt)
        CreateUintRT(ref densityRT, width, height);
        CreateUintRT(ref tempRT, width, height);

        // Hook the texture to the material
        runtimeMat.SetTexture("_DensityU32", densityRT);
        runtimeMat.SetFloat("_Max", exposureMax);
    }

    void CreateUintRT(ref RenderTexture rt, int w, int h)
    {
        if (rt != null) rt.Release();
        rt = new RenderTexture(w, h, 0, RenderTextureFormat.RInt)
        {
            enableRandomWrite = true,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        rt.Create();
    }

    void BindStatics()
    {
        densityCS.SetInt(WidthID, width);
        densityCS.SetInt(HeightID, height);

        densityCS.SetTexture(kClear, "_DensityU32", densityRT);
        densityCS.SetTexture(kAcc, "_DensityU32", densityRT);

        densityCS.SetTexture(kBlurH, "_DensityReadU32", densityRT);
        densityCS.SetTexture(kBlurH, "_TempU32", tempRT);
        densityCS.SetTexture(kBlurV, "_TempU32", tempRT);
        densityCS.SetTexture(kBlurV, "_DensityU32", densityRT);
    }

    void LateUpdate()
    {
        // ----- 1) Fetch positions (NativeArray<Vector2>) from your provider -----
        NativeArray<Vector3> positions = default;
        try
        {
            // Expect a method NativeArray<Vector2> getPositions()
            positions = positionsProvider.GetPositions();
        }
        catch
        {
            Debug.LogError("Failed to call getPositions(): ensure it returns NativeArray<Vector2>.");
            return;
        }
        int count = positions.IsCreated ? positions.Length : 0;

        // Resize GPU buffer if needed
        if (pointsBuffer == null || pointsBuffer.count != Mathf.Max(count, 1))
        {
            AllocateAll(count);
            BindStatics();
        }

        // ----- 2) Auto-bounds (optional) -----
        Vector2 minB = worldMin, maxB = worldMax;
        if (autoBoundsEveryFrame && count > 0)
        {
            minB = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            maxB = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            // NOTE: This is O(N) on CPU; OK up to ~100k. If higher, consider a minmax compute pass.
            for (int i = 0; i < count; i++)
            {
                var p = positions[i];
                if (p.x < minB.x) minB.x = p.x;
                if (p.y < minB.y) minB.y = p.y;
                if (p.x > maxB.x) maxB.x = p.x;
                if (p.y > maxB.y) maxB.y = p.y;
            }
            // Add a small margin
            Vector2 size = maxB - minB;
            Vector2 pad = 0.02f * new Vector2(Mathf.Max(size.x, 1e-3f), Mathf.Max(size.y, 1e-3f));
            minB -= pad; maxB += pad;
        }

        // ----- 3) Upload data & set per-frame params -----
        if (count > 0)
            pointsBuffer.SetData(positions);

        densityCS.SetBuffer(kAcc, PointsID, pointsBuffer);
        densityCS.SetInt(PointCountID, count);
        densityCS.SetInt(SplatRadiusID, Mathf.Clamp(splatRadius, 0, 8));
        densityCS.SetFloat(ScaleID, Mathf.Max(contribution, 0f));
        densityCS.SetVector(WorldMinID, new Vector4(minB.x, minB.y, 0, 0));
        densityCS.SetVector(WorldMaxID, new Vector4(maxB.x, maxB.y, 0, 0));

        // ----- 4) Clear → Accumulate → (optional) Blur -----
        int N = width * height;
        densityCS.Dispatch(kClear, (N + 255) / 256, 1, 1);

        densityCS.Dispatch(kAcc, (count + 255) / 256, 1, 1);

        if (enableBlur)
        {
            int gx = (width + 7) / 8;
            int gy = (height + 7) / 8;
            densityCS.Dispatch(kBlurH, gx, gy, 1);
            densityCS.Dispatch(kBlurV, gx, gy, 1);
        }

        // ----- 5) Feed material -----
        runtimeMat.SetTexture("_DensityU32", densityRT);
        runtimeMat.SetFloat("_Max", Mathf.Max(1e-3f, exposureMax));
    }

    void OnDisable()
    {
        pointsBuffer?.Release();
        pointsBuffer = null;
        if (densityRT) densityRT.Release();
        if (tempRT) tempRT.Release();
    }
}
