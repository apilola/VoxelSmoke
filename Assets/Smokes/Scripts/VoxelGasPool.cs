using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections.LowLevel.Unsafe;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class VoxelGasPool : MonoBehaviour
{
    public uint m_PrewarmCount = 0;
    public VoxelGasJobs m_Prefab;
    [System.NonSerialized] public List<VoxelGasJobs> m_Pool = new List<VoxelGasJobs>();

    CollisionGrid m_CollisionGrid = new CollisionGrid();

    [Header("Collision Grid Settings")]
    [SerializeField] bool LoadGrid = true;
    [SerializeField] public float m_VoxelSize = .1f;
    [SerializeField] LayerMask m_LayerMask = 1;

    static VoxelGasPool m_Instance = null;

    public static CollisionGrid CollisionGrid
    {
        get
        {
            if(m_Instance != null)
            {
                return m_Instance.m_CollisionGrid;
            }

            return null;
        }
    }


    private void Start()
    {
        if(LoadGrid)
        {
            m_Instance = this;
            m_CollisionGrid.Build(m_VoxelSize, m_LayerMask, gameObject.scene);
        }

        for (var i = 0; i < m_PrewarmCount; i++)
        {
            var item = Instantiate<VoxelGasJobs>(m_Prefab);
            item.Allocate();
            Push(item);
        }
    }

    private void OnDrawGizmos()
    {

        if(UnityEditor.EditorApplication.isPlaying && m_Instance == this)
        {
            Vector3 voxelSize = new Vector3(m_VoxelSize, m_VoxelSize, m_VoxelSize);
            Vector3 halfVoxelSize = voxelSize * .5f;

            Vector3 chunkSize = voxelSize * 8;
            Vector3 halfChunkSize = chunkSize * .5f;

            if (m_CollisionGrid.TryGetVoxelInfo(transform.position, out var voxelInfo))
            {
                var col = Gizmos.color;
                var pos = m_CollisionGrid.Bounds.min + (Vector3)voxelInfo.ChunkID * m_VoxelSize * 8;
                var chunkPos = pos + Vector3.one * m_VoxelSize * 8*.5f;
                Gizmos.DrawWireCube(chunkPos, new Vector3(m_VoxelSize, m_VoxelSize, m_VoxelSize)*8);

                Gizmos.color = Color.red;
                //var voxelPos = pos + (Vector3)voxelInfo.VoxelID * m_VoxelSize + Vector3.one * m_VoxelSize * .5f;
                var min = pos + halfVoxelSize;
                var voxelPos = min;
                int voxelID = 0;
                for(var z = 0; z < 8; z++)
                {
                    voxelPos.y = min.y;
                    for(var y = 0; y < 8; y++)
                    {
                        voxelPos.x = min.x;
                        for(var x = 0; x < 8; x++)
                        {
                            if(m_CollisionGrid.Chunks[voxelInfo.ChunkIndex].GetBit(voxelID))
                            {
                                Gizmos.DrawWireCube(voxelPos, voxelSize);
                            }

                            voxelPos.x += m_VoxelSize;
                            voxelID++;
                        }
                        voxelPos.y += m_VoxelSize;
                    }
                    voxelPos.z += m_VoxelSize;
                }
                Gizmos.color = col;
            }
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

    [ContextMenu("Run Collision Grid Test")]
    public void RunCollisionGridTest()
    {
        m_CollisionGrid.Build(m_VoxelSize, m_LayerMask, this.gameObject.scene);
        m_CollisionGrid.Dispose();
    }

    private void OnDestroy()
    {
        m_CollisionGrid.Dispose();
    }
}
