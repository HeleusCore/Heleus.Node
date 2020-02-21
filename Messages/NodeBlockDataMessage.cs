using System;
using Heleus.Base;
using Heleus.Chain;
using Heleus.Chain.Blocks;

namespace Heleus.Messages
{
    public class NodeBlockDataMessage : NodeMessage
    {
        public ChainType ChainType { get; private set; }
        public long BlockId { get; private set; }
        public int ChainId { get; private set; }
        public uint ChainIndex { get; private set; }

        public BlockData BlockData { get; private set; }

        public NodeBlockDataMessage() : base(NodeMessageTypes.BlockData)
        {
        }

        public NodeBlockDataMessage(BlockData blockData) : this()
        {
            ChainType = blockData.ChainType;
            BlockId = blockData.Block.BlockId;
            ChainId = blockData.Block.ChainId;
            BlockData = blockData;
        }

        public NodeBlockDataMessage(ChainType chainType, long blockId, int chainId, uint chainIndex, BlockData blockData) : this()
        {
            ChainType = chainType;
            BlockId = blockId;
            ChainId = chainId;
            ChainIndex = chainIndex;
            BlockData = blockData;
        }

        public NodeBlockDataMessage(ChainType chainType, long blockId, int chainId, uint chainIndex) : this()
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

            if(packer.Pack(BlockData != null))
            {
                var data = BlockData.ToByteArray();
                packer.Pack(data, data.Length);
            }
        }

        protected override void Unpack(Unpacker unpacker)
        {
            base.Unpack(unpacker);
            ChainType = (ChainType)unpacker.UnpackByte();
            BlockId = unpacker.UnpackLong();
            ChainId = unpacker.UnpackInt();
            ChainIndex = unpacker.UnpackUInt();

            if(unpacker.UnpackBool())
            {
                BlockData = BlockData.Restore(unpacker);
            }
        }
    }
}
