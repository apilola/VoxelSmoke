using System.Collections;
using System.Collections.Generic;
using System.Runtime.ConstrainedExecution;
using Unity.Mathematics;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

public class SpawnEffectController : MonoBehaviour
{
    public List<Material> materials = new List<Material>();
    public List<Renderer> renderers = new List<Renderer>();

    public Shader shader = null;
    [SerializeField] Bounds bounds = new Bounds();
    // Start is called before the first frame update

    [SerializeField] PlaneProperty plane0;
    PlaneProperty plane1;
    NoiseProperty noise;
    KeywordBoolProperty effectEnabled;
    KeywordBoolProperty alphaTest;

    [SerializeField] float planeThickness0 = .34f;
    [SerializeField] float planeThickness1 = .2f;

    [SerializeField] float timeScale = 1.0f;

    private void Awake()
    {
        GetComponentsInChildren<Renderer>(renderers);
        bool hasAssignedBounds = false;
        bounds = new Bounds();
        for(var i = renderers.Count - 1; i >= 0; i--)
        {
            var renderer = renderers[i];

            foreach (var material in renderer.materials)
            {
                if (material.shader == shader)
                {
                    materials.Add(material);
                }
            }

            if (!hasAssignedBounds)
            {
                bounds = renderer.bounds;
                hasAssignedBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        plane0 = new PlaneProperty(0, this);
        plane1 = new PlaneProperty(1, this);
        alphaTest = new KeywordBoolProperty(new LocalKeyword(shader, "_ALPHATEST_ON"), this);
        effectEnabled = new KeywordBoolProperty(new LocalKeyword(shader, "_EFFECTENABLED"), this);
        noise = new NoiseProperty(this);
    }

    private void OnEnable()
    {
        bool hasAssignedBounds = false;
        for (var i = renderers.Count - 1; i >= 0; i--)
        {
            var renderer = renderers[i];
            if (!hasAssignedBounds)
            {
                bounds = renderer.bounds;
                hasAssignedBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }
        {
            Vector3 from = bounds.min - new Vector3(planeThickness0, planeThickness0, planeThickness0);
            Vector3 to = bounds.max + new Vector3(planeThickness0, planeThickness0, planeThickness0);
            plane0.Position = from;
            StartCoroutine(Execute(plane0, from, to, .5f, 2f));
        }
        {
            Vector3 from = bounds.min - new Vector3(planeThickness1, planeThickness1, planeThickness1);
            Vector3 to = bounds.max + new Vector3(planeThickness1, planeThickness1, planeThickness1);
            plane0.Position = from;
            StartCoroutine(Execute(plane1, from, to, 0, 1.5f));
        }
        {
            Vector3 to = bounds.min - new Vector3(planeThickness1, planeThickness1, planeThickness1);
            Vector3 from = bounds.max + new Vector3(planeThickness1, planeThickness1, planeThickness1);
            StartCoroutine(Execute(plane1, from, to, 1.5f, 1f));
        }
        {
            float from = 0;
            float to = .7f;
            noise.Alpha = 0;
            StartCoroutine(Execute(noise, from, to, 1, .5f));
        }
        {
            float from = .7f;
            float to = 0;
            StartCoroutine(Execute(noise, from, to, 1.5f, 1f));
        }
        {
            alphaTest.Enabled = true;
            effectEnabled.Enabled = true;
            StartCoroutine(Execute(false, 2.5f, ()=>
            {
                alphaTest.Enabled = false;
                effectEnabled.Enabled = false;
            }));
            //effectEnabled.Enabled = false;
        }

    }

    IEnumerator Execute(bool value, float delay, System.Action action)
    {
        yield return new WaitForSeconds(delay/timeScale);

        action?.Invoke();
    }

    IEnumerator Execute(PlaneProperty plane, Vector3 from, Vector3 to, float delay, float duration)
    {
        yield return new WaitForSeconds(delay/timeScale);
        float step = 1f / duration;
        float time = 0;
        float totalTime = 0; 
        while(time <= 1)
        {
            time += step * Time.deltaTime*timeScale;
            totalTime += Time.deltaTime*timeScale;
            plane.Position = Vector3.Lerp(from, to, Mathf.Clamp01(time));
            yield return null;
        }
    }

    IEnumerator Execute(NoiseProperty noise, float from, float to, float delay, float duration)
    {
        yield return new WaitForSeconds(delay / timeScale);
        float step = 1f / duration;
        float time = 0;
        float totalTime = 0;
        while (time <= 1)
        {
            time += step * Time.deltaTime * timeScale;
            totalTime += Time.deltaTime*timeScale;
            noise.Alpha = Mathf.Lerp(from, to, Mathf.Clamp01(time));
            yield return null;
        }
    }

    private void OnDisable()
    {
        StopAllCoroutines();
    }
}

[System.Serializable]
public class PlaneProperty
{
    [SerializeField] Vector3 position;
    [SerializeField] Vector3 normal;
    [SerializeField] float thickness;

    SpawnEffectController controller;
    const string ID_PREFIX = "_Plane"; 
    const string POSITION_FORMAT = ID_PREFIX + "Position_{0}";
    const string NORMAL_FORMAT = ID_PREFIX + "Normal_{0}";
    const string THICKNESS_FORMAT = ID_PREFIX + "Thickness_{0}";

    int planeID; 
    int positionID = 0;
    int normalID = 0;
    int thicknessID = 0;

    public PlaneProperty(int planeID, SpawnEffectController controller)
    {
        this.controller = controller;
        this.planeID = planeID;
        this.positionID = Shader.PropertyToID(string.Format(POSITION_FORMAT, planeID));
        this.normalID = Shader.PropertyToID(string.Format(NORMAL_FORMAT, planeID));
        this.thicknessID = Shader.PropertyToID(string.Format(THICKNESS_FORMAT, planeID));
        this.Position = Vector3.one;
    }

    public Vector3 Position
    {
        get { return position; }
        set
        {
            if (value != position)
            {
                position = value;
                foreach (Material material in controller.materials)
                {
                    material.SetVector(positionID, position);
                }
                
            }
        }
    }

    public Vector3 Normal
    {
        get { return normal; }
        set
        {
            if (value != normal)
            {
                normal = value;
                foreach (Material material in controller.materials)
                {
                    material.SetVector(normalID, normal);
                }
            }
        }
    }

    public float Thickness
    {
        get { return thickness; }
        set
        {
            if(value != thickness)
            {
                thickness = value;
                foreach(Material material in controller.materials)
                {
                    material.SetFloat(thicknessID, thickness);
                }
            }
        }
    }
}

public class NoiseProperty
{
    float alpha;
    SpawnEffectController controller;

    int noiseID;
    public NoiseProperty(SpawnEffectController controller)
    {
        this.controller = controller;
        this.noiseID = Shader.PropertyToID("_NoiseAlpha");
        alpha = -1f;
    }

    public float Alpha { 
        get { return alpha; } 
        set 
        { 
            if(alpha != value) 
            {
                alpha = value;
                foreach(Material material in controller.materials)
                {
                    material.SetFloat(noiseID, alpha);
                }
            }
        } 
    }
}


[System.Serializable]
public class KeywordBoolProperty
{
    bool enabled;
    SpawnEffectController controller;
    LocalKeyword keyword;

    public KeywordBoolProperty(LocalKeyword id, SpawnEffectController controller)
    {
        this.keyword = id;
        this.controller = controller;
        this.enabled = true;
    }

    public bool Enabled
    {
        get {
            return enabled;
        }
        set
        {
            if(enabled != value)
            {
                enabled = value;

                foreach (Material material in controller.materials)
                {
                    material.SetKeyword(keyword, value);
                }
            }
        }
    }
}