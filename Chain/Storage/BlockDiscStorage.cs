using System;
using System.IO;
using Heleus.Base;
using Heleus.Manager;

namespace Heleus.Chain.Storage
{
    class BlockDiscStorage : DiscStorage
    {
        public ushort Version = Protocol.Version;
        public readonly long SliceIndex;

        public BlockDiscStorage(Base.Storage storage, ChainType chainType, int chainId, uint chainIndex, long sliceIndex, bool readOnly) : base(storage, Path.Combine(BlockStorage.GetBlockStoragePath(chainType, chainId, chainIndex), sliceIndex.ToString()), 256, 32, DiscStorageFlags.AppendOnly | DiscStorageFlags.DynamicBlockSize | (readOnly ? DiscStorageFlags.Readonly : DiscStorageFlags.None))
        {
            SliceIndex = sliceIndex;

            if(UserDataUnpacker.UnpackBool())
            {
                UserDataUnpacker.UnpackUshort();
            }
        }

        public override void Commit()
        {
            UserDataPacker.Position = 0;
            UserDataPacker.Pack(true);
            UserDataPacker.Pack(Version);

            base.Commit();
        }
    }
}
