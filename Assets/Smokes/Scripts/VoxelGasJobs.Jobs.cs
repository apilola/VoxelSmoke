using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
public partial class VoxelGasJobs
{
    [BurstCompile()]
    struct VoxelGasJob : IJobParallelFor
    {
        [ReadOnly]
        public NativeList<Vector3Int> dirtyVoxels;
        [ReadOnly]
        public NativeBitArray voxelFrontier;

        [WriteOnly]
        public NativeQueue<RaycastGuideData>.ParallelWriter hotCellsQueue;


        public float voxelSize;
        public Vector3 minimum;
        public int frontierBounds;
        public uint seed;
        public QueryParameters queryParameters;

        public VoxelGasJob(NativeList<Vector3Int> dirtyVoxels,
                           NativeBitArray voxelFrontier,
                           NativeQueue<RaycastGuideData>.ParallelWriter hotCells,
                           float voxelSize,
                           Vector3 minimum,
                           int frontierBounds,
                           uint seed,
                           QueryParameters queryParameters)
        {
            this.dirtyVoxels = dirtyVoxels;
            this.voxelFrontier = voxelFrontier;
            this.hotCellsQueue = hotCells;
            this.voxelSize = voxelSize;
            this.minimum = minimum;
            this.frontierBounds = frontierBounds;
            this.seed = seed;
            this.queryParameters = queryParameters;
        }

        public void Execute(int index)
        {
            Unity.Mathematics.Random rnd = new Unity.Mathematics.Random(seed + 5556 * (uint)index + 1);
            var directions = new NativeArray<Vector3Int>(13, Allocator.Temp);
            directions[0] = new Vector3Int(0, 1, 0); // Up
            directions[1] = new Vector3Int(0, -1, 0); // Down
            directions[2] = new Vector3Int(0, -1, 0); // Down
            directions[3] = new Vector3Int(0, -1, 0); // Down
            directions[4] = new Vector3Int(0, -1, 0); // Down
            directions[5] = new Vector3Int(-1, 0, 0); // Left
            directions[6] = new Vector3Int(-1, 0, 0); // Left
            directions[7] = new Vector3Int(1, 0, 0); // Right
            directions[8] = new Vector3Int(1, 0, 0); // Right
            directions[9] = new Vector3Int(0, 0, -1); // Front
            directions[10] = new Vector3Int(0, 0, -1); // Front
            directions[11] = new Vector3Int(0, 0, 1); // Back
            directions[12] = new Vector3Int(0, 0, 1); //back

            //Directions array only includes the up direction once to prevent it from growing too tall.
            /*
            Vector3Int[] directions = {
                new Vector3Int(0, 1, 0), // Up
                new Vector3Int(0, -1, 0), // Down
                new Vector3Int(0, -1, 0), // Down
                new Vector3Int(0, -1, 0), // Down
                new Vector3Int(0, -1, 0), // Down
                new Vector3Int(-1, 0, 0), // Left
                new Vector3Int(-1, 0, 0), // Left
                new Vector3Int(1, 0, 0), // Right
                new Vector3Int(1, 0, 0), // Right
                new Vector3Int(0, 0, -1), // Front
                new Vector3Int(0, 0, -1), // Front
                new Vector3Int(0, 0, 1), // Back
                new Vector3Int(0, 0, 1) // Back
            };
            */
            Vector3Int currentVoxel = dirtyVoxels[index];

            Vector3 currentPos = GetVoxelWorldPosition(currentVoxel);

            // Shuffle the directions array to introduce randomness
            ShuffleArray(directions, rnd);

            // Check a random subset of neighbors for available space
            int numNeighborsToCheck = rnd.NextInt(3, 6);
            for (int j = 0; j < numNeighborsToCheck; j++)
            {
                Vector3Int direction = directions[j];
                Vector3Int neighbor = currentVoxel + direction;
                int neighborLocation = GetVoxelIndex(neighbor);

                if (!CheckVoxelVisited(neighborLocation))
                {
                    hotCellsQueue.Enqueue(new RaycastGuideData()
                    {
                        origin = currentPos,
                        voxelSize = voxelSize,
                        direction = direction,
                        position = neighbor
                    });
                }
            }
            directions.Dispose();
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

        bool CheckVoxelVisited(int location)
        {
            if (location < 0)
                return true;
            return voxelFrontier.IsSet(location);
        }

        Vector3 GetVoxelWorldPosition(Vector3Int position)
        {
            float x = position.x * voxelSize + minimum.x;
            float y = position.y * voxelSize + minimum.y;
            float z = position.z * voxelSize + minimum.z;

            return new Vector3(x, y, z);
        }

        private void ShuffleArray<T>(T[] array, Unity.Mathematics.Random rnd)
        {
            for (int i = array.Length - 1; i > 0; i--)
            {
                int j = rnd.NextInt(0, i + 1);
                T temp = array[i];
                array[i] = array[j];
                array[j] = temp;
            }
        }
        private void ShuffleArray<T>(NativeArray<T> array, Unity.Mathematics.Random rnd) where T : struct
        {
            for (int i = array.Length - 1; i > 0; i--)
            {
                int j = rnd.NextInt(0, i + 1);
                T temp = array[i];
                array[i] = array[j];
                array[j] = temp;
            }
        }
    }

    [BurstCompile()]
    struct SpherecastPrepJob : IJob
    {
        public NativeQueue<RaycastGuideData> hotCellQueue;

        public NativeList<OverlapSphereCommand> commandsData;

        public NativeList<ColliderHit> resultsData;

        [WriteOnly]
        public NativeList<Vector3Int> hotCellPositions;

        public QueryParameters queryParameters;

        public SpherecastPrepJob(NativeQueue<RaycastGuideData> hotCellQueue,
                                 NativeList<OverlapSphereCommand> commandsData,
                                 NativeList<ColliderHit> resultsData,
                                 NativeList<Vector3Int> hotCellPositions,
                                 QueryParameters queryParameters)
        {
            this.hotCellQueue = hotCellQueue;
            this.commandsData = commandsData;
            this.resultsData = resultsData;
            this.hotCellPositions = hotCellPositions;
            this.queryParameters = queryParameters;
        }

        public void Execute()
        {
            resultsData.Clear();
            commandsData.Clear();
            hotCellPositions.Clear();

            while (hotCellQueue.TryDequeue(out RaycastGuideData guide))
            {
                commandsData.Add(new OverlapSphereCommand(
                    guide.origin + guide.direction * guide.voxelSize,
                    guide.voxelSize * .45f,
                    queryParameters
                    ));
                resultsData.Add(new());
                hotCellPositions.Add(guide.position);
            }
            /*
            if(resultsData.Capacity < commandsData.Length)
            {
                resultsData.SetCapacity(commandsData.Length);
                Debug.Log("SetCapacity Length " + resultsData.Capacity);
            }*/
        }
    }

    [BurstCompile()]
    struct ProcessSpherecastResultsJob : IJob
    {
        [ReadOnly]
        public NativeList<OverlapSphereCommand> commandsData;

        [ReadOnly]
        public NativeList<ColliderHit> resultsData;

        [ReadOnly]
        public NativeList<Vector3Int> hotCellPostions;

        [WriteOnly]
        public NativeList<Vector3Int> dirtyVoxels;

        [WriteOnly]
        public NativeArray<Vector3> voxelPositionArray;

        NativeBitArray voxelFrontier;

        public int voxelCount;
        public float voxelSize;
        public int frontierBounds;
        public int maxVoxels;
        public Vector3 minimum;

        public ProcessSpherecastResultsJob(NativeList<OverlapSphereCommand> commandsData,
                                           NativeList<ColliderHit> resultsData,
                                           NativeList<Vector3Int> hotCells,
                                           NativeList<Vector3Int> dirtyVoxels,
                                           NativeArray<Vector3> voxelPositionArray,
                                           NativeBitArray voxelFrontier,
                                           int voxelCount,
                                           float voxelSize,
                                           int frontierBounds,
                                           int maxVoxels,
                                           Vector3 minimum)
        {
            this.commandsData = commandsData;
            this.resultsData = resultsData;
            this.hotCellPostions = hotCells;
            this.dirtyVoxels = dirtyVoxels;
            this.voxelPositionArray = voxelPositionArray;
            this.voxelFrontier = voxelFrontier;
            this.voxelCount = voxelCount;
            this.voxelSize = voxelSize;
            this.frontierBounds = frontierBounds;
            this.maxVoxels = maxVoxels;
            this.minimum = minimum;
        }

        public void Execute()
        {
            dirtyVoxels.Clear();

            for (var i = 0; i < commandsData.Length; i++)
            {
                var command = commandsData[i];
                var result = resultsData[i];
                var pos = hotCellPostions[i];

                var voxelIndex = GetVoxelIndex(pos);
                if (result.instanceID == 0 && !CheckVoxelVisited(voxelIndex))
                {
                    AddVoxel(pos);
                }
                SetVoxelVisited(voxelIndex);
            }
        }

        private void AddVoxel(Vector3Int position)
        {
            if (voxelCount < maxVoxels)
            {
                dirtyVoxels.Add(position);
                var worldPos = GetVoxelWorldPosition(position);
                voxelPositionArray[voxelCount] = worldPos;
                voxelCount++;
            }
        }

        private void GetVoxel(Vector3 position)
        {
            if (voxelCount < maxVoxels)
            {
                voxelPositionArray[voxelCount] = position;
                voxelCount++;
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

        Vector3 GetVoxelWorldPosition(Vector3Int position)
        {
            float x = position.x * voxelSize + minimum.x;
            float y = position.y * voxelSize + minimum.y;
            float z = position.z * voxelSize + minimum.z;

            return new Vector3(x, y, z);
        }

        void SetVoxelVisited(int location)
        {
            voxelFrontier.Set(location, true);
        }

        bool CheckVoxelVisited(int location)
        {
            if (location < 0)
                return true;
            return voxelFrontier.IsSet(location);
        }
    }

    public struct VoxelInfo
    {
        public Vector3Int FrontierID;
        public int FrontierIndex;
        public Vector3Int ChunkID;
        public int ChunkIndex;
        public Vector3Int VoxelID;
        public int VoxelIndex;
    }

    public static readonly Vector3Int ChunkSize = new Vector3Int(8, 8, 8);

    public const int DIRECTION_COUNT = 6;
    public unsafe struct DirectionData<T>
    {

        public T Up, Down, Left, Right, Front, Back;

        public T this[int index]
        {
            get
            {
                switch (index)
                {
                    case 0:
                        return Up;
                    case 1:
                        return Down;
                    case 2:
                        return Left;
                    case 3:
                        return Right;
                    case 4:
                        return Front;
                    case 5:
                        return Back;
                    default:
                        throw new System.NotImplementedException("Index Not Supported");
                }
            }
            set
            {
                switch (index)
                {
                    case 0:
                        Up = value;
                        break;
                    case 1:
                        Down = value;
                        break;
                    case 2:
                        Left = value;
                        break;
                    case 3:
                        Right = value;
                        break;
                    case 4:
                        Front = value;
                        break;
                    case 5:
                        Back = value;
                        break;
                    default:
                        throw new System.NotImplementedException("Index Not Supported");
                }
            }
        }
    }

    public static readonly DirectionData<Vector3Int> Directions = new DirectionData<Vector3Int>()
    {
        Up = new Vector3Int(0, 1, 0), //Up
        Down = new Vector3Int(0, -1, 0), //Down
        Left = new Vector3Int(-1, 0, 0), //Left
        Right = new Vector3Int(1, 0, 0), //Right
        Front = new Vector3Int(0, 0, -1), //Front
        Back = new Vector3Int(0, 0, 1) //back
    };

    public static readonly DirectionData<float> DirectionRate = new DirectionData<float>()
    {
        Up = .76f, //Up
        Down = .23f, //Down
        Left = .153f, //Left
        Right = 153f, //Right
        Front = 153f, //Front
        Back = 153f //back
    };


    public static int CalcVolumeIndex(Vector3Int dir, Vector3Int size)
    {
        return dir.x + size.x * (dir.y + dir.z * size.y);
    }

    public static DirectionData<int> CreateOffsetData(Vector3Int volumeSize)
    {
        var data = new DirectionData<int>()
        {
            Up = CalcVolumeIndex(new Vector3Int(0, 1, 0), volumeSize),
            Down = CalcVolumeIndex(new Vector3Int(0, -1, 0), volumeSize),
            Left = CalcVolumeIndex(new Vector3Int(-1, 0, 0), volumeSize),
            Right = CalcVolumeIndex(new Vector3Int(1, 0, 0), volumeSize),
            Front = CalcVolumeIndex(new Vector3Int(0, 0, -1), volumeSize),
            Back = CalcVolumeIndex(new Vector3Int(0, 0, 1), volumeSize)
        };
        return data;
    }

    public static readonly DirectionData<int> VoxelDirectionOffset = CreateOffsetData(new Vector3Int(8, 8, 8));


    [BurstCompile()]
    struct VoxelGasBakedJob : IJob
    {
        public NativeBitArray voxelFrontier;
        [ReadOnly]
        public NativeArray<Chunk> Chunks;
        public NativeQueue<VoxelInfo> hotVoxelsQueue;
        public NativeArray<Vector3> voxelPositionArray;

        public float voxelSize;
        public Vector3 minimum;
        public Vector3Int frontierBounds;
        public uint seed;

        public NativeArray<int> count;
        public int MaxVolume;

        public Vector3Int SceneSize;

        public DirectionData<int> FrontierDirectionOffsets;
        public DirectionData<int> ChunkDirectionOffsets;

        public void Execute()
        {
            Unity.Mathematics.Random rnd = new Unity.Mathematics.Random(seed + 5556);

            //Directions array only includes the up direction once to prevent it from growing too tall.
            var directions = new NativeArray<int>(13, Allocator.Temp);
            directions[0] = 0; // Up
            directions[1] = 1; // Down
            directions[2] = 1; // Down
            directions[3] = 1; // Down
            directions[4] = 1; // Down
            directions[5] = 2; // Left
            directions[6] = 2; // Left
            directions[7] = 3; // Right
            directions[8] = 3; // Right
            directions[9] = 4; // Front
            directions[10] = 4; // Front
            directions[11] = 5; // Back
            directions[12] = 5; //back

            while (count[0] < MaxVolume && hotVoxelsQueue.TryDequeue(out var current))
            {
                ShuffleArray(directions, rnd);
                int numNeighborsToExpandTo = rnd.NextInt(3, 6);
                for (int j = 0; j < numNeighborsToExpandTo && count[0] < MaxVolume; j++)
                {
                    int directionIndex = directions[j];
                    Vector3Int direction = Directions[directionIndex];

                    if (TryGetNeighborInDirection(current, directionIndex, out var neighbor))
                    {
                        if(!Chunks[neighbor.ChunkIndex].GetBit(neighbor.VoxelIndex))
                        {
                            hotVoxelsQueue.Enqueue(neighbor);
                        //we might be able to speed this up by keeping track of the voxel's position in voxelInfo instead of calculating it here.
                            voxelPositionArray[count[0]] = minimum + (Vector3) neighbor.FrontierID * voxelSize;
                            count[0]++;
                        }
                        else
                        {

                        }
                    }
                }
            }
        }

        bool TryGetNeighborInDirection(VoxelInfo current, int directionIndex, out VoxelInfo neighbor)
        {
            Vector3Int direction = Directions[directionIndex];
            neighbor = current;

            neighbor.FrontierID += direction;

            //check if we are inside the frontier bounds
            if(IsNotInVoxelBounds(neighbor.FrontierID, frontierBounds))
            {
                return false;
            }

            //update our frontier index
            neighbor.FrontierIndex += FrontierDirectionOffsets[directionIndex];

            //check if we visited this voxel before
            if (CheckVoxelVisited(neighbor.FrontierIndex))
            {
                return false;
            }
            voxelFrontier.Set(neighbor.FrontierIndex, true);

            //move to the next voxel
            neighbor.VoxelID += direction;

            //check if we are in our chunks bounds
            if(IsNotInVoxelBounds(neighbor.VoxelID, ChunkSize))
            {
                //move to the next chunk
                neighbor.ChunkID += direction;
                if(IsNotInVoxelBounds(neighbor.ChunkID, SceneSize))
                {
                    return false;
                }

                //update our chunk index
                neighbor.ChunkIndex += ChunkDirectionOffsets[directionIndex];

                //update our voxel id
                switch (directionIndex)
                {
                    case 0:
                        neighbor.VoxelID.y = 0;
                        break;
                    case 1:
                        neighbor.VoxelID.y = 7;
                        break;
                    case 2:
                        neighbor.VoxelID.x = 7;
                        break;
                    case 3:
                        neighbor.VoxelID.x = 0;
                        break;
                    case 4:
                        neighbor.VoxelID.z = 7;
                        break;
                    case 5:
                        neighbor.VoxelID.z = 0;
                        break;
                    default:
                        throw new System.NotImplementedException("Index Not Supported");
                }

                //I'm lazy so I'll just throw out our current index and calculate a new one
                neighbor.VoxelIndex = CalcVolumeIndex(neighbor.VoxelID, ChunkSize);
                return true;
            }
            else
            {
                //update our voxel index
                neighbor.VoxelIndex += VoxelDirectionOffset[directionIndex];
                return true;
            }
        }

        bool IsNotInVoxelBounds(Vector3Int id, Vector3Int size)
        {
            return id.x >= size.x ||
                id.x < 0 ||
                id.y >= size.y ||
                id.y < 0 ||
                id.z >= size.z ||
                id.z < 0;
        }

        bool CheckVoxelVisited(int location)
        {
            if (location < 0)
                return true;
            return voxelFrontier.IsSet(location);
        }

        Vector3 GetVoxelWorldPosition(Vector3Int position)
        {
            float x = position.x * voxelSize + minimum.x;
            float y = position.y * voxelSize + minimum.y;
            float z = position.z * voxelSize + minimum.z;

            return new Vector3(x, y, z);
        }

        private void ShuffleArray<T>(NativeArray<T> array, Unity.Mathematics.Random rnd) where T : struct
        {
            for (int i = array.Length - 1; i > 0; i--)
            {
                int j = rnd.NextInt(0, i + 1);
                T temp = array[i];
                array[i] = array[j];
                array[j] = temp;
            }
        }
    }
}