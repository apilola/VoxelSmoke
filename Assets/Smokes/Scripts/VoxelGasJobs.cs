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
    Texture3D volumeTexture;

    NativeBitArray voxelFrontier;
    NativeQueue<VoxelFrontierInfo> hotVoxelsQueue;
    NativeList<Vector3Int> hotCellPositions;
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
        
        /*
        volumeTexture = new Texture3D(frontierBounds,
                                            frontierBounds,
                                            frontierBounds,
                                            UnityEngine.Experimental.Rendering.GraphicsFormat.R16_UInt, //Choose r16 because max wave count could be around 90
                                            UnityEngine.Experimental.Rendering.TextureCreationFlags.DontInitializePixels                                            
                                            );

        volumeTexture.SetPixelData<ushort>(voxelFrontier);
        */
        
        RenderTextureDescriptor rtd = new RenderTextureDescriptor()
        {
            dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
            height = frontierBounds,
            width = frontierBounds,
            volumeDepth = frontierBounds,
            enableRandomWrite = true,
            graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8_UNorm // choose r8 cause its a floating point value and we don't have much accuracy anyway.
        };
        /*
        var tex = new RenderTexture(rtd);
        tex.
        ComputeShader s;

        var k = s.FindKernel("s");
        int id = Shader.PropertyToID("P");
        s.SetTexture(k, "S", rtd);
        ComputeBuffer cb = new ComputeBuffer(int count, ComputeBufferType.Structured, ComputeBufferMode.SubUpdates)
        s.Dispatch(k, 0, 0, 0);

        RenderTexture t = new RenderTexture(rtd);
        t.Create();
        */

        queryParameters = new QueryParameters(obstacleLayerMask, true, QueryTriggerInteraction.Ignore, false);

        //initialize job data structures
        voxelFrontier = new NativeBitArray(frontierBounds * frontierBounds * frontierBounds, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        hotVoxelsQueue = new NativeQueue<VoxelFrontierInfo>(Allocator.Persistent);
        voxelPositionArray = new NativeArray<Vector3>(maxVoxels, Allocator.Persistent);
        hotCellPositions = new NativeList<Vector3Int>(Allocator.Persistent);
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
        var wp = GetVoxelWorldPosition(center);

        if (VoxelGasPool.CollisionGrid != null && VoxelGasPool.CollisionGrid.TryGetVoxelInfo(wp, out var info))
        {
            VoxelFrontierInfo frontierInfo = info;
            frontierInfo.FrontierIndex = voxelIndex;
            frontierInfo.FrontierID = center;

            var val = VoxelGasPool.CollisionGrid.Chunks[info.ChunkIndex].GetBit(info.VoxelIndex);
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
        DebugDraw();
        StartCoroutine(RunExpansion());
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

    private void OnDestroy()
    {
        firstBatchJobHandle.Complete();
        secondBatchJobHandle.Complete();

        voxelFrontier.Dispose();
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