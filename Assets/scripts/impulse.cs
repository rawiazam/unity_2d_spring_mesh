using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Profiling;
using UnityEngine.UIElements;

public class impulse : MonoBehaviour
{
    public float impulseStrength = 10;
    public float cutoff = 4;
    public float time_factor = 1;

    private float current_time;

    private spring_mesh springMesh;

    private Vector2 prevPos = new Vector2(float.NaN, float.NaN);


    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        springMesh = FindFirstObjectByType<spring_mesh>();
    }

    // Update is called once per frame
    void Update()
    {
        Vector2 mousePosition = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        if (Input.GetMouseButton(0) || Input.GetMouseButton(1))
        {
            current_time = math.max(current_time + Time.fixedDeltaTime, 0.1f);
            Vector2 target;
            target = mousePosition;
            // }
            NativeArray<Vector2> velocities = springMesh.GetVelocities();
            NativeArray<Vector3> positions = springMesh.GetPositions();
            // for (int i = 0; i < positions.Length; i++)
            // {
            //     Vector2 position = positions[i];
            //     Vector2 velocity = velocities[i];
            //     Vector2 direction = position - target;
            //     float distance = Vector2.Distance(position, target);
            //     if (distance > cutoff || distance > cutoff * current_time / time_factor)
            //     {
            //         continue;
            //     }
            //     float impulse = impulseStrength / distance;
            //     if (Input.GetMouseButton(1))
            //     {
            //         impulse = -impulse;
            //     }
            //     velocities[i] = velocity + impulse * direction;

            // }
            // foreach (Vector3 position in springMesh.GetPoints())
            // {
            //     Vector3 direction = (Vector2)point.position - target;
            //     float distance = Vector2.Distance(point.position, target);
            //     if (distance > cutoff || distance > cutoff * current_time / time_factor)
            //     {
            //         continue;
            //     }
            //     float impulse = impulseStrength / distance;
            //     if (Input.GetMouseButton(1))
            //     {
            //         impulse = -impulse;
            //     }
            //     point.GetComponent<mesh_point>().AddVelocity(impulse * direction);
            // }
            Profiler.BeginSample("query");
            List<int> indecies = springMesh.spatialHash.Query(target, cutoff, positions);
            NativeArray<int> indeciesArr = new(indecies.Count, Allocator.TempJob);
            for (int i = 0; i < indecies.Count; i++)
            {
                indeciesArr[i] = indecies[i];
            }
            Profiler.EndSample();
            Profiler.BeginSample("impulse change velocities");
            Vector2 mouseVector = (mousePosition - prevPos).normalized;

            new ApplyForces
            {
                positions = positions,
                velocities = velocities,
                indecies = indeciesArr,
                impulseStrength = impulseStrength,
                mousePosition = mousePosition,
                mouseVector = mouseVector,
                mouseButton1 = Input.GetMouseButton(1),
                deltaTime = Time.deltaTime,
            }.Schedule(indeciesArr.Length, 64).Complete();

            indeciesArr.Dispose();

            Profiler.EndSample();
        }
        else
        {
            current_time = 0;
        }
        prevPos = mousePosition;
    }
    [BurstCompile]
    private struct ApplyForces : IJobParallelFor
    {
        [NativeDisableContainerSafetyRestriction]
        public NativeArray<Vector3> positions;
        [NativeDisableContainerSafetyRestriction]
        public NativeArray<Vector2> velocities;
        public NativeArray<int> indecies;
        public float impulseStrength;
        public Vector2 mousePosition;
        public Vector2 mouseVector;
        public bool mouseButton1;
        public float deltaTime;

        public void Execute(int id)
        {
            int index = indecies[id];
            Vector2 position = positions[index];
            float impulse = impulseStrength / Vector2.Distance(position, mousePosition);
            if (mouseButton1)
            {
                impulse = -impulse;
                mouseVector = Vector2.zero;
            }
            Vector2 spreadDirection = (position - mousePosition).normalized;
            Vector2 direction = (spreadDirection + mouseVector * 2).normalized;
            velocities[index] = velocities[index] + impulse * direction;
        }
    }
}
