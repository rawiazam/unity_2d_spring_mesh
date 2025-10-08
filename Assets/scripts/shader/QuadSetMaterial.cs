using UnityEngine;


//This is for debugging
public class QuadSetMaterial : MonoBehaviour
{
    public Material mat;
    DensityView densityView;
    SpriteRenderer spriteRenderer;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        densityView = FindFirstObjectByType<DensityView>();
        spriteRenderer = GetComponent<SpriteRenderer>();
        spriteRenderer.material.SetTexture("_DensityTex", densityView.GetDensityTexture());
    }
}
