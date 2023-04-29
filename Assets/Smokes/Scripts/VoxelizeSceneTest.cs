using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using System.Runtime.InteropServices;
using Unity.Jobs;
using Unity.Collections.LowLevel.Unsafe;

public class VoxelizeSceneTest : MonoBehaviour
{
    [SerializeField] Bounds m_Bounds;
    [SerializeField] float m_VoxelSize = .1f;
    [SerializeField] LayerMask m_ObstacleMask = 1;

    private void OnDrawGizmos()
    {
        var orig = Gizmos.color;
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireCube(m_Bounds.center, m_Bounds.size);
        Gizmos.color = orig;
    }

    [ContextMenu("Recalculate Bounds")]
    public void RecalculateVoxels()
    {
        var rootObjects = this.gameObject.scene.GetRootGameObjects();
        if (CalculateBounds(rootObjects, out m_Bounds))
        {
            ConstructVoxels();
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
                if (!startingBoundsFound && GetChildRendererBounds(rootObjects[i], out bounds))
                {
                    startingBoundsFound = true;
                }
                else if (GetChildRendererBounds(rootObjects[i], out var aabb))
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

    bool GetChildRendererBounds(GameObject go, out Bounds bounds)
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

    private void ConstructVoxels()
    {
        var totalSize = m_Bounds.size; //precalculate bounds of a scene
        float chunkSize = m_VoxelSize * 8;

        var chunks = totalSize / chunkSize;
        Debug.Log($"chunk approx {chunks}");

        int sizeX = Mathf.CeilToInt(chunks.x)+1;
        int sizeY = Mathf.CeilToInt(chunks.y)+1;
        int sizeZ = Mathf.CeilToInt(chunks.z)+1;

        Debug.Log($"{nameof(sizeX)} {sizeX}");
        Debug.Log($"{nameof(sizeY)} {sizeY}");
        Debug.Log($"{nameof(sizeZ)} {sizeZ}");

        totalSize += Vector3.one*chunkSize;
        m_Bounds.size = totalSize;

        int count = sizeX * sizeY * sizeZ;
        Debug.Log("Chunk Count: "+ count);

        Vector3 min = m_Bounds.min;
        
        Vector3 chunkPos = min;

        NativeArray<Chunk> chunksArray = new NativeArray<Chunk>(count, Allocator.TempJob); //TODO: extract this to store in locally stored variable

        NativeArray<OverlapSphereCommand>[] commandsArray = new NativeArray<OverlapSphereCommand>[count];
        NativeArray<ColliderHit>[] hitArray = new NativeArray<ColliderHit>[count];
        NativeArray<JobHandle> constructionJobs = new NativeArray<JobHandle>(count, Allocator.Temp);        

        int chunkIndex = 0;
        for (int z = 0; z < sizeZ; z++)
        {
            for(int y = 0; y < sizeY; y++)
            {
                for(int x = 0; x < sizeX; x++)
                {
                    var commands = commandsArray[chunkIndex] = new NativeArray<OverlapSphereCommand>(512, Allocator.TempJob);
                    var hits = hitArray[chunkIndex] = new NativeArray<ColliderHit>(512, Allocator.TempJob);


                    PrepCommandBatch(commands, chunkPos);

                    //Schedule a Job
                    var physicsJob = OverlapSphereCommand.ScheduleBatch(commands, hits, 16, 1);
                    ConstructChunkJob constructJob = new ConstructChunkJob()
                    {
                        Hits = hits,
                        Chunk = chunksArray.GetSubArray(chunkIndex, 1)
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
        chunksArray.Dispose();
    }
    
    public void PrepCommandBatch(NativeArray<OverlapSphereCommand> commands, Vector3 pos)
    {
        QueryParameters queryParams = new QueryParameters(m_ObstacleMask, true, QueryTriggerInteraction.Ignore, true);

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
        }

        //Because the chunk is set to read only but we still need to write to it lets try this.
        //I'm not certain if this actually works.
        var ptr = NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(Chunk);
        UnsafeUtility.WriteArrayElement(ptr, 0, chunk);
    }
}