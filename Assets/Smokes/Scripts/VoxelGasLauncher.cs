using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class VoxelGasLauncher : MonoBehaviour
{
    public VoxelGasPool m_Pool;
    public Camera m_Camera;

    public float m_LaunchVelocity = 700;

    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        var current = Mouse.current;
        if(current.leftButton.wasPressedThisFrame)
        {
            StartCoroutine(LaunchProjectile());
        }
    }

    IEnumerator LaunchProjectile()
    {
        var screenPos = new Vector3Int(m_Camera.scaledPixelWidth / 2, m_Camera.scaledPixelHeight / 2);
        var ray = m_Camera.ScreenPointToRay(screenPos);
        var gas = m_Pool.Pop();
        gas.gameObject.SetActive(true);

        gas.transform.position = ray.origin;
        var rb = gas.GetComponent<Rigidbody>();
        rb.isKinematic = false;
        rb.AddForce(ray.direction * m_LaunchVelocity);
        yield return new WaitForSeconds(2);

        rb.isKinematic = true;
        gas.enabled = true;

        yield return new WaitForSeconds(10);

        m_Pool.Push(gas);        
    }
}
