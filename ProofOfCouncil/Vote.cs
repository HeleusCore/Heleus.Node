using System;
using System.Collections.Generic;
using Heleus.Base;
using Heleus.Chain.Blocks;
using Heleus.Cryptography;

namespace Heleus.ProofOfCouncil
{
    public enum VoteResultTypes
    {
        Ok,
        InvalidTransactions,
        InvalidBlockId,
        InvalidIssuer,
        InvalidRevision,
        InvalidCouncilId,
        EmptyProposal
    }

    public class Vote : IPackable
    {
        public readonly VoteResultTypes Result;

        public readonly long Timestamp;
        public readonly Hash BlockHash;
        public readonly long BlockId;
        public readonly int BlockRevision;
        public readonly short BlockIssuer;

        public readonly short VoteIssuer;

        public readonly HashSet<long> RejectedTransactionIds = new HashSet<long>();

        public Vote(VoteResultTypes result, Block block, long blockId, int revision, short blockIssuer, short voteIssuer, HashSet<long> rejected)
        {
            Timestamp = Time.Timestamp;
            Result = result;
            BlockId = blockId;
            BlockRevision = revision;

            BlockHash = block?.BlockHash;
            
            BlockIssuer = blockIssuer;
            VoteIssuer = voteIssuer;
            if (rejected != null)
                RejectedTransactionIds = rejected;
        }

        public Vote(Unpacker unpacker)
        {
            Result = (VoteResultTypes)unpacker.UnpackByte();
            unpacker.Unpack(out Timestamp);
            if(unpacker.UnpackBool())
                unpacker.Unpack(out BlockHash);
            
            unpacker.Unpack(out BlockId);
            unpacker.Unpack(out BlockRevision);
            unpacker.Unpack(out BlockIssuer);
            unpacker.Unpack(out VoteIssuer);

            unpacker.Unpack(RejectedTransactionIds);
        }

        public void Pack(Packer packer)
        {
            packer.Pack((byte)Result);
            packer.Pack(Timestamp);
            if(packer.Pack(BlockHash != null))
                packer.Pack(BlockHash);
            
            packer.Pack(BlockId);
            packer.Pack(BlockRevision);
            packer.Pack(BlockIssuer);
            packer.Pack(VoteIssuer);

            packer.Pack(RejectedTransactionIds);
        }
    }
}
