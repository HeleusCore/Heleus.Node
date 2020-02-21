using System;
using Heleus.Base;
using Heleus.Chain;
using Heleus.Chain.Blocks;
using Heleus.Cryptography;
using Heleus.ProofOfCouncil;

namespace Heleus.Messages
{
    public class CouncilBlockVoteMessage : CouncilMessage
    {
        public Vote Vote { get; private set; }
        
        public CouncilBlockVoteMessage() : base(CouncilMessageTypes.BlockVote)
        {
        }

        public CouncilBlockVoteMessage(Vote vote, ChainType chainType, int chainId, uint chainIndex, short issuer, Key issuerKey) : base(CouncilMessageTypes.BlockVote, chainType, chainId, chainIndex, issuer, issuerKey)
        {
            Vote = vote;
        }

        protected override void Pack(Packer packer)
        {
            base.Pack(packer);
            packer.Pack(Vote.Pack);
        }

        protected override void Unpack(Unpacker unpacker)
        {
            base.Unpack(unpacker);
            Vote = new Vote(unpacker);
        }
    }
}
