using UnityEngine;
using System.Collections.Generic;
using UnityEngine.VFX;
using Unity.Collections;
using Unity.Jobs;
using static VoxelGasJobs;
using Unity.Collections.LowLevel.Unsafe;

public partial class VoxelGasJobs : MonoBehaviour
{
    public int maxVoxels = 2048;
    [Range(128, 1290)] public int frontierBounds = 512;

    public float voxelSize = .25f;
    public LayerMask obstacleLayerMask;
    public VisualEffect smokeGraph;

    [Header("Debugging")]
    public bool debug;
    public Mesh debugMesh;
    public Material debugMat;
    List<Matrix4x4> debugMats = new List<Matrix4x4>();

    NativeBitArray voxelFrontier;
    NativeList<Vector3Int> dirtyVoxels;
    NativeQueue<RaycastGuideData> hotCellsQueue;
    NativeQueue<VoxelFrontierInfo> hotVoxelsQueue;
    NativeList<Vector3Int> hotCellPositions;
    NativeList<OverlapSphereCommand> commandsData;
    NativeList<ColliderHit> resultsData;
    NativeArray<Vector3> voxelPositionArray;
    NativeArray<VoxelMetaData> voxelMetaData;
    NativeArray<int> countArray;
    NativeArray<int> waveCount;

    GraphicsBuffer voxelPositionBuffer;
    GraphicsBuffer voxelMetaDataBuffer;
    GraphicsBuffer voxelSizeBuffer;

    JobHandle secondBatchJobHandle;
    JobHandle firstBatchJobHandle;

    Vector3 minOffset;
    Vector3Int center;
    QueryParameters queryParameters;
    bool isAllocated = false;

    private int voxelCount = 0;

    Vector3 minimum; //the minimum voxel position.

    bool isFirstBatchDirty = false;
    bool isSecondBatchDirty = false;

    private void Awake()
    {
        Allocate();
    }

    public void Allocate()
    {
        if(isAllocated)
        {
            return;
        }

        queryParameters = new QueryParameters(obstacleLayerMask, true, QueryTriggerInteraction.Ignore, false);

        //initialize job data structures
        voxelFrontier = new NativeBitArray(frontierBounds * frontierBounds * frontierBounds, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        dirtyVoxels = new NativeList<Vector3Int>(frontierBounds, Allocator.Persistent);
        hotCellsQueue = new NativeQueue<RaycastGuideData>(Allocator.Persistent);
        hotVoxelsQueue = new NativeQueue<VoxelFrontierInfo>(Allocator.Persistent);
        voxelPositionArray = new NativeArray<Vector3>(maxVoxels, Allocator.Persistent);
        hotCellPositions = new NativeList<Vector3Int>(Allocator.Persistent);
        commandsData = new NativeList<OverlapSphereCommand>(Allocator.Persistent);
        resultsData = new NativeList<ColliderHit>(Allocator.Persistent);
        countArray = new NativeArray<int>(new int[] { 0 }, Allocator.Persistent);
        voxelMetaData = new NativeArray<VoxelMetaData>(maxVoxels, Allocator.Persistent);
        waveCount = new NativeArray<int>(new int[] { 0 }, Allocator.Persistent);

        voxelPositionBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxVoxels, 12);
        voxelSizeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxVoxels, 4);
        voxelPositionBuffer.SetData(voxelPositionArray);
        voxelMetaDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxVoxels, 16);
        voxelMetaDataBuffer.SetData(voxelMetaData);
        

        //calculate the minimum position offset
        float halfExtent = ((float)frontierBounds / 2f) * voxelSize;
        minOffset = new Vector3(halfExtent, halfExtent, halfExtent) - new Vector3(voxelSize / 2f, voxelSize / 2f, voxelSize / 2f);

        //calculate the center voxel of the voxelFrontier
        var halfExtentInt = Mathf.CeilToInt(frontierBounds / 2);
        center = new Vector3Int(halfExtentInt, halfExtentInt, halfExtentInt);
        
        //Temporary solution to help prevent issues with crevices
        //A better solution would be to supply the grenade with a safe position to expand from
        //this can be done by keeping track of the last empty voxel our grenade occupied.
        center.y += 1; 

        isAllocated = true;
    }

    private void OnEnable()
    {
        if(!isAllocated)
        {
            Allocate();
        }

        //calculate the minimum position
        minimum = transform.position - minOffset;

        // Add a starting voxel at the center of the volume
        var voxelIndex = GetVoxelIndex(center);
        dirtyVoxels.Add(center);
        var wp = GetVoxelWorldPosition(center);

        if (VoxelGasPool.CollisionGrid != null && VoxelGasPool.CollisionGrid.TryGetVoxelInfo(wp, out var info))
        {
            VoxelFrontierInfo frontierInfo = info;
            frontierInfo.FrontierIndex = voxelIndex;
            frontierInfo.FrontierID = center;

            var val = VoxelGasPool.CollisionGrid.Chunks[info.ChunkIndex].GetBit(info.VoxelIndex);
            /*
            Debug.Log($"VoxelInfo: has collision {val}");
            Debug.Log("ChunkID: " + info.ChunkID);
            Debug.Log("ChunkIndex: " + info.ChunkIndex);
            Debug.Log("VoxelID: " + info.VoxelID);
            Debug.Log("VoxelIndex: " + info.VoxelIndex);
            */
            voxelMetaData[voxelCount] = new VoxelMetaData()
            {
                waveIndex = 0,
                position = wp,
            };
            waveCount[0]++;
            hotVoxelsQueue.Enqueue(frontierInfo);
        }

        GetVoxel(wp);
        SetVoxelVisited(voxelIndex);
    }

    private void OnDisable()
    {
        firstBatchJobHandle.Complete();
        secondBatchJobHandle.Complete();

        voxelFrontier.Clear();
        dirtyVoxels.Clear();
        hotCellsQueue.Clear();
        commandsData.Clear();
        resultsData.Clear();
        hotCellPositions.Clear();
        hotVoxelsQueue.Clear();
        ClearNativeArray(voxelMetaData);
        ClearNativeArray(voxelPositionArray);
        waveCount[0] = 0;
        countArray[0] = 0;

        voxelCount = 0;
        isFirstBatchDirty = false;
        isSecondBatchDirty = false;

        if (smokeGraph != null && smokeGraph.visualEffectAsset != null)
        {
            smokeGraph.Stop();
            smokeGraph.enabled = false;
        }
    }

    int GetVoxelIndex(Vector3Int position)
    {
        if (position.x >= frontierBounds ||
            position.x < 0 ||
            position.y >= frontierBounds ||
            position.y < 0 ||
            position.z >= frontierBounds ||
            position.z < 0)
        {
            return -1;
        }
        return position.x + position.y * frontierBounds + position.z * frontierBounds * frontierBounds;
    }

    void SetVoxelVisited(int location)
    {
        voxelFrontier.Set(location, true);
    }

    Vector3 GetVoxelWorldPosition(Vector3Int position)
    {
        float x = position.x * voxelSize + minimum.x;
        float y = position.y * voxelSize + minimum.y;
        float z = position.z * voxelSize + minimum.z;

        return new Vector3(x, y, z);
    }

    private void Update()
    {
        if (VoxelGasPool.CollisionGrid == null)
        {
            Update_OLD();
        }
        else
        {
            DebugDraw();
            StartCoroutine(RunExpansion());
        }
    }

    System.Collections.IEnumerator RunExpansion()
    { 
        firstBatchJobHandle.Complete(); //this is here because if you pause the editor the yield instruction below never completes that frame

        var voxelGasBakedJob = new VoxelGasBakedJob()
        {
            voxelFrontier = voxelFrontier,
            Chunks = VoxelGasPool.CollisionGrid.Chunks,
            hotVoxelsQueue = hotVoxelsQueue,
            voxelPositionArray = voxelPositionArray,
            voxelMetaData = voxelMetaData,
            voxelSize = voxelSize,
            waveCount = waveCount,
            minimum = minimum,
            frontierBounds = new Vector3Int(frontierBounds, frontierBounds, frontierBounds),
            seed = (uint)(UnityEngine.Random.value * uint.MaxValue),
            voxelCount = countArray,
            MaxVolume = maxVoxels,
            SceneSize = VoxelGasPool.CollisionGrid.Size,
            FrontierDirectionOffsets = CreateOffsetData(new Vector3Int(frontierBounds, frontierBounds, frontierBounds)),
            ChunkDirectionOffsets = CreateOffsetData(VoxelGasPool.CollisionGrid.Size),
        };


        firstBatchJobHandle = voxelGasBakedJob.Schedule();
        JobHandle.ScheduleBatchedJobs();

        yield return new WaitForEndOfFrame();

        firstBatchJobHandle.Complete();
        voxelCount = countArray[0];

        if(smokeGraph != null && smokeGraph.visualEffectAsset != null)
        {
            smokeGraph.enabled = true;
            smokeGraph.SetInt("waveCount", waveCount[0]);
            smokeGraph.SetInt("voxelCount", countArray[0]);
            voxelMetaDataBuffer.SetData(voxelMetaData);
            smokeGraph.SetGraphicsBuffer("voxelMetaData", voxelMetaDataBuffer);
            smokeGraph.SetFloat("voxelSize", voxelSize);
            smokeGraph.Play();
        }
        isFirstBatchDirty = false;
    }

    private void DebugDraw()
    {
        if (debug)
        {
            debugMats.Clear();
            var scale = Vector3.one * voxelSize;

            for (var i = 0; i < voxelCount; i++)
            {
                debugMats.Add(Matrix4x4.TRS(voxelPositionArray[i], Quaternion.identity, scale));
            }

            Graphics.DrawMeshInstanced(debugMesh, 0, debugMat, debugMats);
        }
    }

    private void Update_OLD()
    {
        if (isSecondBatchDirty)
        {
            secondBatchJobHandle.Complete();

            isSecondBatchDirty = false;
            voxelCount += dirtyVoxels.Length;
        }

        DebugDraw();

        // Stop expanding if maximum number of voxels reached
        if (dirtyVoxels.Length > 0 && voxelCount < maxVoxels)
        {
            RunFirstJobBatch();
        }
    }

    private void RunFirstJobBatch()
    {
        var voxelGasJob = new VoxelGasJob(
                   dirtyVoxels,
                   voxelFrontier,
                   hotCellsQueue.AsParallelWriter(),
                   voxelSize,
                   minimum,
                   frontierBounds,
                   (uint)(UnityEngine.Random.value * uint.MaxValue),
                   queryParameters
               );
        var voxelGasJobHandle = voxelGasJob.Schedule(dirtyVoxels.Length, 8);

        var spherecastPrepJob = new SpherecastPrepJob(
            hotCellsQueue,
            commandsData,
            resultsData,
            hotCellPositions,
            queryParameters);

        firstBatchJobHandle = spherecastPrepJob.Schedule(voxelGasJobHandle);
        JobHandle.ScheduleBatchedJobs();

        isFirstBatchDirty = true;
    }

    private void RunSecondJobBatch()
    {
        if (VoxelGasPool.CollisionGrid == null)
        {
            var spherecastJobHandle = OverlapSphereCommand.ScheduleBatch(commandsData.AsArray(), resultsData.AsArray(), 8, 1);

            //var spherecastJobHandle = SpherecastCommand.ScheduleBatch(commandsData.AsDeferredJobArray(), resultsData.AsDeferredJobArray(), 8, spherecastPrepJobHandle);

            var processSpherecastResultJob = new ProcessSpherecastResultsJob(
               commandsData,
               resultsData,
               hotCellPositions,
               dirtyVoxels,
               voxelPositionArray,
               voxelFrontier,
               voxelCount,
               voxelSize,
               frontierBounds,
               maxVoxels,
               minimum
           );

            secondBatchJobHandle = processSpherecastResultJob.Schedule(spherecastJobHandle);
            JobHandle.ScheduleBatchedJobs();
            isSecondBatchDirty = true;
        }
        else
        {
            voxelCount = countArray[0];
        }
    }

    private void LateUpdate()
    {
        if (VoxelGasPool.CollisionGrid == null)
        {
            LateUpdateOld();
        }
    }

    private void LateUpdateOld()
    {
        if (isFirstBatchDirty)
        {
            firstBatchJobHandle.Complete();
            isFirstBatchDirty = false;

            RunSecondJobBatch();
        }
    }

    private void OnDestroy()
    {
        firstBatchJobHandle.Complete();
        secondBatchJobHandle.Complete();

        voxelFrontier.Dispose();
        dirtyVoxels.Dispose();
        hotCellsQueue.Dispose();
        commandsData.Dispose();
        resultsData.Dispose();
        hotCellPositions.Dispose();
        voxelPositionArray.Dispose();
        hotVoxelsQueue.Dispose();
        countArray.Dispose();
        voxelMetaData.Dispose();
        waveCount.Dispose();

        voxelPositionBuffer?.Dispose();
        voxelSizeBuffer?.Dispose();
        voxelMetaDataBuffer?.Dispose();

        isAllocated = false;
    }

    private void GetVoxel(Vector3 position)
    {
        if (voxelCount < maxVoxels)
        {             
            voxelPositionArray[voxelCount] = position;
            voxelCount++;
            countArray[0]++;
        }
    }

    public struct RaycastGuideData
    {
        public Vector3Int position;
        public Vector3 origin;
        public Vector3 direction;
        public float voxelSize;
    }

    unsafe void ClearNativeArray<T>(NativeArray<T> array) where T: struct
    {
        void* ptr = array.GetUnsafePtr();
        UnsafeUtility.MemClear(ptr, array.Length);
    }
}