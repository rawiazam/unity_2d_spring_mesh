using UnityEngine;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine.Rendering;
using System.Linq;
using UnityEngine.AI;
using System.Collections.ObjectModel;
using Unity.Burst;
using UnityEngine.Profiling;
using Unity.Jobs;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class dynamic_mesh : MonoBehaviour
{
    public int rows = 5;
    public int cols = 5;

    public Material mat;

    private Mesh mesh;
    private List<int> indices;
    public Color color;

    // Assume this is filled each frame by your job/compute shader
    public NativeArray<Vector3> positions;

    NativeArray<Vector3> output;
    GraphicsBuffer uploadBuffer;
    GraphicsBuffer vertexBuffer;

    void Oestroy()
    {
        positions.Dispose();
    }

    void Start()
    {
        mesh = new Mesh();
        mesh.MarkDynamic(); // Important for frequent updates
        mesh.indexFormat = IndexFormat.UInt32;
        GetComponent<MeshFilter>().mesh = mesh;

        // Make sure MeshRenderer has a simple material
        var renderer = GetComponent<MeshRenderer>();
        if (renderer.sharedMaterial == null)
        {
            // Built-in Unlit/Color works in most pipelines
            // renderer.material = new Material(Shader.Find("Unlit/test"));
            // renderer.material.color = Color.green;
            renderer.material = mat;
        }


        BuildIndices(); // only once
        output = new NativeArray<Vector3>(positions.Length, Allocator.Persistent);
        mesh.vertices = positions.ToArray();
        mesh.SetIndices(indices, MeshTopology.Lines, 0);
        uploadBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Vertex | GraphicsBuffer.Target.CopySource, GraphicsBuffer.UsageFlags.LockBufferForWrite, positions.Length, sizeof(float) * 3);
        mesh.vertexBufferTarget |= GraphicsBuffer.Target.CopyDestination;
        vertexBuffer = mesh.GetVertexBuffer(0);
    }

    void BuildIndices()
    {
        indices = new List<int>();

        // Horizontal lines
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols - 1; c++)
            {
                int i0 = r * cols + c;
                int i1 = r * cols + (c + 1);
                indices.Add(i0);
                indices.Add(i1);
            }
        }

        // Vertical lines
        for (int c = 0; c < cols; c++)
        {
            for (int r = 0; r < rows - 1; r++)
            {
                int i0 = r * cols + c;
                int i1 = (r + 1) * cols + c;
                indices.Add(i0);
                indices.Add(i1);
            }
        }
    }

    public void UpdateMesh()
    {
        if (!positions.IsCreated) return;

        // mesh.Clear();
        //mesh.vertices = Vector2To3(positions).ToArray();//positions.Select(v => (Vector3)v).ToArray();
        // mesh.SetVertexBufferData(Vector2To3(positions), 0, 0, positions.Length, 0);
        Profiler.BeginSample("translating to vector3");
        // new Expand2To3Job { In2 = positions, Out3 = output }.Schedule(positions.Length, 256).Complete();
        Profiler.EndSample();
        Profiler.BeginSample("setting data");
        NativeArray<Vector3> mapped = uploadBuffer.LockBufferForWrite<Vector3>(0, positions.Length);
        mapped.CopyFrom(positions);
        // mesh.RecalculateBounds();
        uploadBuffer.UnlockBufferAfterWrite<Vector3>(positions.Length);
        Graphics.CopyBuffer(uploadBuffer, vertexBuffer);
        Profiler.EndSample();
        // Built-in Unlit/Color works in most pipelines
        // renderer.material = new Material(Shader.Find("Unlit/Color"));
        // Renderer renderer = GetComponent<Renderer>();
        // renderer.material.color = color;
    }

    [BurstCompile]
    NativeArray<Vector3> Vector2To3(NativeArray<Vector2> vecs)
    {
        for (int i = 0; i < vecs.Length; i++)
            output[i] = vecs[i];
        return output;
    }


    [BurstCompile]
    private struct Expand2To3Job : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Vector2> In2;
        public NativeArray<Vector3> Out3; // mapped upload buffer
        public void Execute(int i)
        {
            var p = In2[i];
            Out3[i] = new Vector3(p.x, p.y, 0f);
        }
    }
    // void FixedUpdate()
    // {
    //     BuildIndices();
    // }
}
