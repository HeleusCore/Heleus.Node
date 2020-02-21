using System;
using Heleus.Chain.Blocks;
using Heleus.Manager;

namespace Heleus.Chain.Storage
{
    public class BlockSliceInfo
    {
        public static long GetSliceIndex(long blockId)
        {
            if (blockId < Protocol.GenesisBlockId)
                throw new ArgumentException("Invalid Block id", nameof(blockId));

            return blockId / Protocol.BlockSplitCount;
        }

        public static long GetFirstBlockIdFromSliceIndex(long sliceIndex)
        {
            if (sliceIndex < 0)
                throw new ArgumentException("Invalid slice index", nameof(sliceIndex));

            return sliceIndex * Protocol.BlockSplitCount;
        }

        public readonly long SliceIndex;

        public long FirstBlockId;
        public long LastBlockId;

        public bool Valid => (Count > 0) && (FirstBlockId % Protocol.BlockSplitCount == 0) && (GetSliceIndex(FirstBlockId) == SliceIndex);
        public bool Finalized => Valid && (Count == Protocol.BlockSplitCount);

        public long Count
        {
            get
            {
                if (FirstBlockId < Protocol.GenesisBlockId)
                    return 0;

                return LastBlockId - FirstBlockId + 1;
            }
        }

        public BlockSliceInfo(long sliceIndex)
        {
            SliceIndex = sliceIndex;
        }
    }
}
