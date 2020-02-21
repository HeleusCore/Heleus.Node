using System;
using Heleus.Base;
using Heleus.Operations;

namespace Heleus.Chain.Storage
{
    public class TransactionSliceInfo
    {
        public static long GetSliceIndex(long transactionId)
        {
            if (transactionId < Operation.FirstTransactionId)
                return -1;

            return ((transactionId - 1) / Protocol.TransactionSplitCount);
        }

        public static bool IsSliceSplit(long transactionId)
        {
            if (transactionId < Operation.FirstTransactionId)
                return false;

            return (transactionId % Protocol.TransactionSplitCount) == 0;
        }

        public readonly int SliceId;

        public readonly long FirstBlockId;
        public readonly long LastBlockId;

        public readonly bool Finalized;

        public readonly long FirstTransactionId;
        public readonly long LastTransactionId;
        public readonly long TransactionCount;

        public TransactionSliceInfo(int sliceid, long firstBlockId, long lastBlockId, bool finalized, long length, long startIndex, long endIndex)
        {
            SliceId = sliceid;
            FirstBlockId = firstBlockId;
            LastBlockId = lastBlockId;
            Finalized = finalized;
            TransactionCount = length;
            if (length > 0)
            {
                FirstTransactionId = startIndex;
                LastTransactionId = endIndex;
            }
        }
    }
}
