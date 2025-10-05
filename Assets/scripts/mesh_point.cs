using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.VisualScripting;
using UnityEngine;

public class mesh_point : MonoBehaviour
{
    public Vector2 velocity
    {
        get
        {
            return velocities[pointIndex];
        }
        set
        {
            velocities[pointIndex] = value;
        }
    }
    public float mass = 1;

    public bool isStatic = false;

    public int pointIndex;
    public NativeArray<Vector2> velocities;
    public NativeArray<Vector3> positions;

    private Transform trans;


    void Start()
    {
        trans = gameObject.transform;
    }

    // Update is called once per frame
    public void TestUpdate()
    {
        if (!velocities.IsCreated) return;
        if (!isStatic && velocity.magnitude > 0.01f)
        {
            velocity = Mathf.Min(velocity.magnitude, 6) * velocity.normalized;
            Vector3 newPos = trans.position + (Vector3)velocity * Mathf.Clamp(Time.deltaTime, 0, Time.fixedDeltaTime);
            trans.position = newPos;
            if (velocity.magnitude < 0.003f)
            {
                velocity = Vector2.zero;
            }
            velocity *= 0.99f;
            velocities[pointIndex] = velocity;
            positions[pointIndex] = newPos;
        }
        else
        {
            positions[pointIndex] = trans.position;
        }
    }

    public void AddVelocity(Vector2 force)
    {
        velocity += force / mass;
    }
}
