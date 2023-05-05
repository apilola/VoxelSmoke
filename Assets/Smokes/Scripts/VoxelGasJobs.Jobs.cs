using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.VFX;

public partial class VoxelGasJobs
{
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    [VFXType(VFXTypeAttribute.Usage.GraphicsBuffer)]
    public struct VoxelMetaData
    {
        public int waveIndex;
        public Vector3 position;
    }

    public struct VoxelFrontierInfo
    {
        public Vector3Int FrontierID;
        public int FrontierIndex;
        public Vector3Int ChunkID;
        public int ChunkIndex;
        public Vector3Int VoxelID;
        public int VoxelIndex;
        public int waveIndex;

        public static implicit operator VoxelInfo(VoxelFrontierInfo info) 
        {
            return new VoxelInfo()
            {
                ChunkIndex = info.ChunkIndex,
                ChunkID = info.ChunkID,
                VoxelID = info.VoxelID,
                VoxelIndex = info.VoxelIndex,
            };
        }

        public static implicit operator VoxelFrontierInfo(VoxelInfo info)
        {
            return new VoxelFrontierInfo()
            {
                ChunkIndex = info.ChunkIndex,
                ChunkID = info.ChunkID,
                VoxelID = info.VoxelID,
                VoxelIndex = info.VoxelIndex,
            };
        }
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
        public NativeQueue<VoxelFrontierInfo> hotVoxelsQueue;
        public NativeArray<Vector3> voxelPositionArray;
        public NativeArray<VoxelMetaData> voxelMetaData;
        public NativeArray<int> waveCount;
        public NativeArray<int> voxelCount;

        public float voxelSize;
        public Vector3 minimum;
        public Vector3Int frontierBounds;
        public uint seed;
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

            while (voxelCount[0] < MaxVolume && hotVoxelsQueue.TryDequeue(out var current))
            {
                ShuffleArray(directions, rnd);
                int numNeighborsToExpandTo = rnd.NextInt(3, 6);
                for (int j = 0; j < numNeighborsToExpandTo && voxelCount[0] < MaxVolume; j++)
                {
                    int directionIndex = directions[j];
                    Vector3Int direction = Directions[directionIndex];

                    if (TryGetNeighborInDirection(current, directionIndex, out var neighbor))
                    {
                        if(!Chunks[neighbor.ChunkIndex].GetBit(neighbor.VoxelIndex))
                        {
                            hotVoxelsQueue.Enqueue(neighbor);
                        //we might be able to speed this up by keeping track of the voxel's position in voxelInfo instead of calculating it here.
                            var pos = voxelPositionArray[voxelCount[0]] = minimum + (Vector3) neighbor.FrontierID * voxelSize;
                            voxelMetaData[voxelCount[0]] = new VoxelMetaData()
                            {
                                waveIndex = neighbor.waveIndex,
                                position = pos,                                
                            };
                            voxelCount[0]++;
                            if (waveCount[0] - 1 < neighbor.waveIndex)
                            {
                                waveCount[0] = neighbor.waveIndex + 1;
                            }
                        }
                        else
                        {

                        }
                    }
                }
            }
            directions.Dispose();
        }

        bool TryGetNeighborInDirection(VoxelFrontierInfo current, int directionIndex, out VoxelFrontierInfo neighbor)
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
            }
            else
            {
                //update our voxel index
                neighbor.VoxelIndex += VoxelDirectionOffset[directionIndex];
            }
            neighbor.waveIndex++;
            return true;
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