using System;
using Heleus.Base;
using Heleus.Chain;
using Heleus.Chain.Blocks;
using Heleus.Cryptography;

namespace Heleus.Messages
{
    public class CouncilBlockSignatureMessage : CouncilMessage
    {
        public BlockSignatures BlockSignatures { get; private set; }
        public BlockProposalSignatures ProposalSignatures { get; private set; }
        
        public CouncilBlockSignatureMessage() : base(CouncilMessageTypes.BlockSignature)
        {
        }

        public CouncilBlockSignatureMessage(BlockSignatures signatures, BlockProposalSignatures proposalSignatures, ChainType chainType, int chainId, uint chainIndex, short issuer, Key issuerKey) : base(CouncilMessageTypes.BlockSignature, chainType, chainId, chainIndex, issuer, issuerKey)
        {
            BlockSignatures = signatures;
            ProposalSignatures = proposalSignatures;
        }

        protected override void Pack(Packer packer)
        {
            base.Pack(packer);
            BlockSignatures.Pack(packer);
            ProposalSignatures.Pack(packer);
        }

        protected override void Unpack(Unpacker unpacker)
        {
            base.Unpack(unpacker);
            BlockSignatures = new BlockSignatures(unpacker);
            ProposalSignatures = new BlockProposalSignatures(unpacker);
        }
    }
}
