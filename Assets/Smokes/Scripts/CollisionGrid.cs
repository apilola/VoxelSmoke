using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using System.Runtime.InteropServices;
using Unity.Jobs;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine.SceneManagement;

public class CollisionGrid : System.IDisposable
{
    [SerializeField] float m_VoxelSize = .1f;
    [SerializeField] Bounds m_Bounds;
    [SerializeField] LayerMask m_LayerMask = 1;
    [SerializeField] Vector3Int m_Size;

    QueryParameters m_QueryParameters;
    NativeArray<Chunk> m_Chunks;

    public ref NativeArray<Chunk> Chunks => ref m_Chunks;
    public Bounds Bounds => m_Bounds;
    public LayerMask LayerMask => m_LayerMask;
    public Vector3Int Size => m_Size;
    public float voxelSize => m_VoxelSize;


    public CollisionGrid() { }


    public void Dispose()
    {
        CleanUp();
    }

    void CleanUp()
    {
        if (m_Chunks.IsCreated)
        {
            //If any Jobs are using this arrray we should wait until those jobs are finished before cleaning up.
            AtomicSafetyHandle handle = NativeArrayUnsafeUtility.GetAtomicSafetyHandle(Chunks);
            var res = AtomicSafetyHandle.EnforceAllBufferJobsHaveCompleted(handle);

            m_Chunks.Dispose();
            m_Chunks = default;

            Debug.Log($"Chunks was disposed with result of {res}");
        }
    }

    public void DrawBounds()
    {
        var orig = Gizmos.color;
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireCube(m_Bounds.center, m_Bounds.size);
        Gizmos.color = orig;
    }

    public void Build(float voxelSize, LayerMask layerMask, Scene scene)
    {
        CleanUp();

        m_VoxelSize = voxelSize;
        m_QueryParameters = new QueryParameters(layerMask, true, QueryTriggerInteraction.Ignore, true);

        var rootObjects = scene.GetRootGameObjects();
        if (CalculateBounds(rootObjects, out m_Bounds))
        {
            ConstructChunks();
        }
    }

    //This function attempts to calculate bounds of the currently loaded scene.
    private bool CalculateBounds(GameObject[] rootObjects, out Bounds bounds)
    {
        bounds = new Bounds();
        if (rootObjects.Length > 0)
        {
            bool startingBoundsFound = false;
            for (int i = 0, count = rootObjects.Length; i < count; i++)
            {
                if (!startingBoundsFound && GetChildBounds(rootObjects[i], out bounds))
                {
                    startingBoundsFound = true;
                }
                else if (GetChildBounds(rootObjects[i], out var aabb))
                {
                    bounds.Encapsulate(aabb);
                }
            }
            return true;
        }
        else
        {
            return false;
        }
    }

    bool GetChildBounds(GameObject go, out Bounds bounds)
    {
        Collider[] components = go.GetComponentsInChildren<Collider>();
        if (components.Length > 0)
        {
            bounds = components[0].bounds;
            for (int i = 1, ni = components.Length; i < ni; i++)
            {
                bounds.Encapsulate(components[i].bounds);
            }
            return true;
        }
        else
        {
            bounds = new Bounds();
            return false;
        }
    }

    private void ConstructChunks()
    {
        var totalSize = m_Bounds.size; //precalculate bounds of a scene
        float chunkSize = m_VoxelSize * 8;

        var chunks = totalSize / chunkSize;
        Debug.Log($"chunk approx {chunks}");

        m_Size.x = Mathf.CeilToInt(chunks.x)+1;
        m_Size.y = Mathf.CeilToInt(chunks.y)+1;
        m_Size.z = Mathf.CeilToInt(chunks.z)+1;

        totalSize += Vector3.one*chunkSize;
        m_Bounds.size = totalSize;

        int count = m_Size.x * m_Size.y * m_Size.z;
        Debug.Log("Chunk Count: "+ count);

        Vector3 min = m_Bounds.min;
        
        Vector3 chunkPos = min;

        m_Chunks = new NativeArray<Chunk>(count, Allocator.Persistent); //TODO: extract this to store in locally stored variable

        NativeArray<OverlapSphereCommand>[] commandsArray = new NativeArray<OverlapSphereCommand>[count];
        NativeArray<ColliderHit>[] hitArray = new NativeArray<ColliderHit>[count];
        NativeArray<JobHandle> constructionJobs = new NativeArray<JobHandle>(count, Allocator.Temp);        

        int chunkIndex = 0;
        for (int z = 0; z < m_Size.z; z++)
        {
            chunkPos.y = min.y;
            for(int y = 0; y < m_Size.y; y++)
            {
                chunkPos.x = min.x;
                for(int x = 0; x < m_Size.x; x++)
                {
                    var commands = commandsArray[chunkIndex] = new NativeArray<OverlapSphereCommand>(512, Allocator.TempJob);
                    var hits = hitArray[chunkIndex] = new NativeArray<ColliderHit>(512, Allocator.TempJob);

                    var prepJob = new PrepCommandBatchJob()
                    {
                        MinPos = chunkPos,
                        VoxelSize = m_VoxelSize,
                        Commands = commands,
                        QueryParameters = new QueryParameters(m_LayerMask, true, QueryTriggerInteraction.Ignore, true)
                    };

                    var prepJobHandle = prepJob.Schedule();

                    //Schedule a Job
                    var physicsJob = OverlapSphereCommand.ScheduleBatch(commands, hits, 16, 1, prepJobHandle);
                    ConstructChunkJob constructJob = new ConstructChunkJob()
                    {
                        Hits = hits,
                        Chunk = m_Chunks.GetSubArray(chunkIndex, 1)
                    };
                    
                    constructionJobs[chunkIndex] = constructJob.Schedule(physicsJob);

                    chunkIndex++;
                    chunkPos.x += chunkSize;
                }

                chunkPos.y += chunkSize;
            }

            chunkPos.z += chunkSize;
        }

        Debug.Log("Scheduled all Jobs");
        JobHandle.CompleteAll(constructionJobs);
        for(var i = 0; i < count; i++)
        {
            commandsArray[i].Dispose();
            hitArray[i].Dispose();
        }

        constructionJobs.Dispose();
    }
    
    public void PrepCommandBatch(NativeArray<OverlapSphereCommand> commands, Vector3 pos)
    {
        QueryParameters queryParams = new QueryParameters(m_LayerMask, true, QueryTriggerInteraction.Ignore, true);

        int i = 0; //commandindex;
        for (int z = 0; z < 8; z++)
        {
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    commands[i] = new OverlapSphereCommand(pos, m_VoxelSize * .5f, queryParams);
                    i++;
                    pos.z += m_VoxelSize;
                }
                pos.y += m_VoxelSize;
            }
            pos.x += m_VoxelSize;
        }
    }

    
    public bool TryGetVoxelInfo(Vector3 position, out VoxelInfo voxelInfo)
    {
        voxelInfo = new VoxelInfo();
        if (!m_Bounds.Contains(position))
        {
            return false;
        }

        Vector3 localPosition = position - m_Bounds.min;

        Vector3Int chunkID = voxelInfo.ChunkID = new Vector3Int(
            Mathf.FloorToInt(localPosition.x / (m_VoxelSize * 8)),
            Mathf.FloorToInt(localPosition.y / (m_VoxelSize * 8)),
            Mathf.FloorToInt(localPosition.z / (m_VoxelSize * 8))
        );

        voxelInfo.ChunkIndex = chunkID.x + Size.x * (chunkID.y + chunkID.z * Size.y);

        var voxelID = voxelInfo.VoxelID = new Vector3Int(
            Mathf.FloorToInt((localPosition.x - chunkID.x * m_VoxelSize * 8) / m_VoxelSize),
            Mathf.FloorToInt((localPosition.y - chunkID.y * m_VoxelSize * 8) / m_VoxelSize),
            Mathf.FloorToInt((localPosition.z - chunkID.z * m_VoxelSize * 8) / m_VoxelSize)
        );

        voxelInfo.VoxelIndex = voxelID.x + voxelID.y * 8 + voxelID.z * 64;

        return true;
    }

}

public struct VoxelInfo
{
    public Vector3Int ChunkID;
    public int ChunkIndex;
    public Vector3Int VoxelID;
    public int VoxelIndex;
}

//A collision chunk is 8x8x8 and 512 voxels total
[StructLayout(LayoutKind.Explicit)]
public unsafe struct Chunk
{
    [FieldOffset(0)]
    public fixed ulong data[8]; //Each BitField is unsigned 64-bit interger.

    public bool GetBit(int pos)
    {
        int d = System.Math.DivRem(pos, 64, out int r);
        return (data[d] & (1ul << r)) != 0;
    }

    public void SetBit(int pos, bool value)
    {
        int d = System.Math.DivRem(pos, 64, out int r);
        var bit = 1ul << r;
        if (value)
            data[d] |= bit;
        else
            data[d] &= ~(bit);
    }
}

public struct ConstructChunkJob : IJob
{
    [ReadOnly]
    public NativeArray<ColliderHit> Hits;

    [ReadOnly] 
    public NativeArray<Chunk> Chunk; //this should be set to a single chunk of the chunk data.

    public unsafe void Execute()
    {
        Chunk chunk = new Chunk();
        for(int i = 0; i < 512; i++)
        {
            chunk.SetBit(i, Hits[i].instanceID != 0);
            /*            if(Hits[i].instanceID != 0)
                        {
                            Debug.Log(Hits[i].instanceID);
                        }*/
        }

        //Because the chunk is set to read only but we still need to write to it lets try this.
        //I'm not certain if this actually works.
        var ptr = NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(Chunk);
        UnsafeUtility.WriteArrayElement(ptr, 0, chunk);
    }
}

public struct PrepCommandBatchParallelJob : IJobParallelFor
{
    public Vector3 MinPos;
    public float VoxelSize;
    public QueryParameters QueryParameters;

    public int SizeX, SizeY, SizeZ;

    [WriteOnly] 
    public NativeArray<OverlapSphereCommand> Commands;

    public void Execute(int index)
    {
        Vector3 voxelPos = MinPos + To3D(index) * VoxelSize;

        Commands[index] = new OverlapSphereCommand(voxelPos, VoxelSize * .5f, QueryParameters);
    }

    public Vector3 To3D(int index)
    {
        int z = index / (SizeX * SizeY);
        index -= (z * SizeX * SizeY);
        int y = index / SizeX;
        int x = index % SizeX;
        return new Vector3Int(x, y, z);
    }
}

public struct PrepCommandBatchJob : IJob
{
    public Vector3 MinPos;
    public float VoxelSize;
    public QueryParameters QueryParameters;

    [WriteOnly]
    public NativeArray<OverlapSphereCommand> Commands;

    public void Execute()
    {
        var min = MinPos + new Vector3(VoxelSize, VoxelSize, VoxelSize) *.5f;
        var pos = min;
        int i = 0; //commandindex;
        for (int z = 0; z < 8; z++)
        {
            pos.y = min.y;
            for (int y = 0; y < 8; y++)
            {
                pos.x = min.x;
                for (int x = 0; x < 8; x++)
                {
                    Commands[i] = new OverlapSphereCommand(pos, VoxelSize * .5f, QueryParameters);
                    i++;
                    pos.x += VoxelSize;
                }
                pos.y += VoxelSize;
            }
            pos.z += VoxelSize;
        }
    }
}