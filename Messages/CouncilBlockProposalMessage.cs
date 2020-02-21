using System;
using Heleus.Base;
using Heleus.Chain;
using Heleus.Chain.Blocks;
using Heleus.Cryptography;

namespace Heleus.Messages
{
    public class CouncilBlockProposalMessage : CouncilMessage
    {
        public long BlockId { get; private set; }
        public int Revision { get; private set; }

        public Block Block { get; private set; }

        public CouncilBlockProposalMessage() : base(CouncilMessageTypes.BlockProposal)
        {
        }

        public CouncilBlockProposalMessage(long blockId, int revision, Block block, ChainType chainType, int chainId, uint chainIndex, short issuer, Key issuerKey) : base(CouncilMessageTypes.BlockProposal, chainType, chainId, chainIndex, issuer, issuerKey)
        {
            BlockId = blockId;
            Revision = revision;
            Block = block;
        }

        protected override void Pack(Packer packer)
        {
            base.Pack(packer);

            packer.Pack(BlockId);
            packer.Pack(Revision);

            if (packer.Pack(Block != null))
            {
                packer.Pack(Block);
            }
        }

        protected override void Unpack(Unpacker unpacker)
        {
            base.Unpack(unpacker);

            BlockId = unpacker.UnpackLong();
            Revision = unpacker.UnpackInt();

            if (unpacker.UnpackBool())
            {
                Block = Block.Restore(unpacker);
            }
        }
    }
}
