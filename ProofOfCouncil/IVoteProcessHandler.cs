using System;
using System.Collections.Generic;
using Heleus.Chain.Blocks;

namespace Heleus.ProofOfCouncil
{
    public interface IVoteProcessHandler<BlockType> where BlockType : Block
    {
        void NewBlockAvailable(VoteProcess<BlockType> voteProcess, BlockData<BlockType> blockData, BlockProposalSignatures blockProposalSignatures);
        BlockType GetBlockProposal(VoteProcess<BlockType> voteProcess, BlockType lastBlock, int revision);
        bool CheckBlockProposal(VoteProcess<BlockType> voteProcess, BlockType block, BlockType lastBlockout, out HashSet<long> invalidTransactionIds);

        void BroadcastCurrentRevision(VoteProcess<BlockType> voteProcess, long blockId, int revision);
        void BroadcastProposal(VoteProcess<BlockType> voteProcess, long blockId, int revision, BlockType blockData);
        void BroadcastVote(VoteProcess<BlockType> voteProcess, Vote vote);
        void BroadcastSignature(VoteProcess<BlockType> voteProcess, BlockSignatures blockSignatures, BlockProposalSignatures blockProposalSignatures);
    }
}
