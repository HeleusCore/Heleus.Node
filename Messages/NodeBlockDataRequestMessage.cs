using System;
using Heleus.Base;
using Heleus.Chain;

namespace Heleus.Messages
{
    public class NodeBlockDataRequestMessage : NodeMessage
    {
        public ChainType ChainType { get; private set; }
        public long BlockId { get; private set; }
        public int ChainId { get; private set; }
        public uint ChainIndex { get; private set; }
        
        public NodeBlockDataRequestMessage() : base(NodeMessageTypes.BlockDataRequest)
        {
        }

        public NodeBlockDataRequestMessage(ChainType chainType, long blockId, int chainId, uint chainIndex) : this()
        {
            ChainType = chainType;
            BlockId = blockId;
            ChainId = chainId;
            ChainIndex = chainIndex;
        }

        protected override void Pack(Packer packer)
        {
            base.Pack(packer);
            packer.Pack((byte)ChainType);
            packer.Pack(BlockId);
            packer.Pack(ChainId);
            packer.Pack(ChainIndex);
        }

        protected override void Unpack(Unpacker unpacker)
        {
            base.Unpack(unpacker);
            ChainType = (ChainType)unpacker.UnpackByte();
            BlockId = unpacker.UnpackLong();
            ChainId = unpacker.UnpackInt();
            ChainIndex = unpacker.UnpackUInt();
        }
    }
}
