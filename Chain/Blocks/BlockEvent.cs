using System;
namespace Heleus.Chain.Blocks
{
    public class BlockEvent<BlockType> where BlockType : Block
    {
        public readonly BlockType Block;
        public readonly BlockSignatures BlockSignature;
        
        public BlockEvent(BlockType block, BlockSignatures blockSignature)
        {
            Block = block;
            BlockSignature = blockSignature;
        }

        public BlockEvent(BlockData<BlockType> blockData)
        {
            Block = blockData.Block;
            BlockSignature = blockData.Signatures;
        }
    }
}
