using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UI;

public class DensityView : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created

    public RawImage debugImage;
    public int maxDensity;
    [Range(0, 10)] public int blurRadius;
    public ComputeShader densityMapCompute;

    private NativeArray<Vector3> positions;
    private SpringMesh mesh;
    private RenderTexture densityTextureA;
    private RenderTexture densityTextureB;
    private ComputeBuffer countBuffer;

    private ComputeBuffer PositionsBuffer;
    private string[] _kernels = {
            "FillDensityMap",
            "CountPoints",
            "ClearCount",
            "BlurH",
            "BlurV",
        };
    private Dictionary<string, int> kernels = new();

    private int2 TextureThreadGroups;

    void OnDestroy()
    {
        densityTextureA?.Release();
        countBuffer?.Release();
        PositionsBuffer?.Release();
    }

    void Awake()
    {
        mesh = FindFirstObjectByType<SpringMesh>();
        int width = Screen.width;
        int height = Screen.height;
        TextureThreadGroups = new int2(Mathf.CeilToInt(width / 8f),
                                       Mathf.CeilToInt(height / 8f));

        densityTextureA = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
        {
            enableRandomWrite = true,
            useMipMap = false,
            autoGenerateMips = false,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };
        densityTextureA.Create();
        densityTextureB = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
        {
            enableRandomWrite = true,
            useMipMap = false,
            autoGenerateMips = false,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };
        densityTextureB.Create();

        countBuffer = new ComputeBuffer(width * height, Marshal.SizeOf<uint>());
    }

    void Start()
    {
        positions = mesh.GetPositions();

        PositionsBuffer = new ComputeBuffer(positions.Length, Marshal.SizeOf<Vector3>());
        if (debugImage)
            debugImage.texture = densityTextureA;

        densityMapCompute.SetInt("flipY", SystemInfo.graphicsUVStartsAtTop ? 1 : 0);
        densityMapCompute.SetInt("_count", positions.Length);
        foreach (string kernel in _kernels)
        {
            int id = densityMapCompute.FindKernel(kernel);
            kernels[kernel] = id;
            densityMapCompute.SetBuffer(id, "positions", PositionsBuffer);
            densityMapCompute.SetBuffer(id, "countBuf", countBuffer);
            densityMapCompute.SetTexture(id, "Density", densityTextureA);
            densityMapCompute.SetTexture(id, "DensityB", densityTextureB);
        }
    }

    // Update is called once per frame
    void LateUpdate()
    {
        if (!PositionsBuffer.IsValid())
            Debug.Log("here");
        PositionsBuffer.SetData(positions);

        // by design, i want to change in the editor
        densityMapCompute.SetInt("maxDensity", maxDensity);
        densityMapCompute.SetInt("blurRadius", blurRadius);
        Matrix4x4 PVMatrix = Camera.main.projectionMatrix * Camera.main.worldToCameraMatrix;
        densityMapCompute.SetMatrix("_WorldToClip", PVMatrix);

        densityMapCompute.Dispatch(kernels["ClearCount"], TextureThreadGroups.x, TextureThreadGroups.y, 1);
        densityMapCompute.Dispatch(kernels["CountPoints"], Mathf.CeilToInt(positions.Length / 256f), 1, 1);
        densityMapCompute.Dispatch(kernels["FillDensityMap"], TextureThreadGroups.x, TextureThreadGroups.y, 1);
        densityMapCompute.Dispatch(kernels["BlurH"], TextureThreadGroups.x, TextureThreadGroups.y, 1);
        densityMapCompute.Dispatch(kernels["BlurV"], TextureThreadGroups.x, TextureThreadGroups.y, 1);
    }


    public RenderTexture GetDensityTexture()
    {
        return densityTextureA;
    }
}
