using System;
using Heleus.Base;
using Heleus.Chain;
using Heleus.Chain.Blocks;
using Heleus.Cryptography;


namespace Heleus.Messages{
    public class CouncilCurrentRevisionMessage : CouncilMessage
    {
        public long BlockId { get; private set; }
        public int Revision { get; private set; }
        
        public CouncilCurrentRevisionMessage() : base(CouncilMessageTypes.CurrentRevision)
        {
        }

        public CouncilCurrentRevisionMessage(long blockId, int revision, ChainType chainType, int chainId, uint chainIndex, short issuer, Key issuerKey) : base(CouncilMessageTypes.CurrentRevision, chainType, chainId, chainIndex, issuer, issuerKey)
        {
            BlockId = blockId;
            Revision = revision;
        }

        protected override void Pack(Packer packer)
        {
            base.Pack(packer);

            packer.Pack(BlockId);
            packer.Pack(Revision);
        }

        protected override void Unpack(Unpacker unpacker)
        {
            base.Unpack(unpacker);

            BlockId = unpacker.UnpackLong();
            Revision = unpacker.UnpackInt();
        }
    }
}
