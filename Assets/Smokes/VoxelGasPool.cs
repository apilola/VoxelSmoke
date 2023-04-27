using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class VoxelGasPool : MonoBehaviour
{
    public uint m_PrewarmCount = 0;
    public VoxelGasJobs m_Prefab;
    [System.NonSerialized] public List<VoxelGasJobs> m_Pool = new List<VoxelGasJobs>();

    private void Start()
    {
        for(var i = 0; i < m_PrewarmCount; i++)
        {
            var item = Instantiate<VoxelGasJobs>(m_Prefab);
            item.Allocate();
            Push(item);
        }
    }

    public VoxelGasJobs Pop()
    {
        if(m_Pool.Count == 0)
        {

            var item = Instantiate<VoxelGasJobs>(m_Prefab);
            item.Allocate();
            item.enabled = false;
            item.gameObject.SetActive(false);
            return item;
        }
        else
        {
            var index = m_Pool.Count - 1;
            var item = m_Pool[index];
            m_Pool.RemoveAt(index);
            item.transform.SetParent(null);
            return item;
        }
    }

    public void Push(VoxelGasJobs item)
    {
        item.enabled = false;
        item.gameObject.SetActive(false);
        item.transform.SetParent(transform);
        m_Pool.Add(item);
    }

}
