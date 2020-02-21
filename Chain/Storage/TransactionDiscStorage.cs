using System;
using Heleus.Base;
using Heleus.Chain.Blocks;

namespace Heleus.Chain.Storage
{
    public class TransactionDiscStorage : DiscStorage
    {
        public ushort Version = Protocol.Version;
        public long FirstBlockId = Protocol.InvalidBlockId;
        public long LastBlockId = Protocol.InvalidBlockId;
        public bool Split;

        public TransactionDiscStorage(Base.Storage storage, string name, int blockSize, DiscStorageFlags flags) : base(storage, name, blockSize, 32, flags)
        {
            if (UserDataUnpacker.UnpackBool())
            {
                UserDataUnpacker.UnpackUshort();
                FirstBlockId = UserDataUnpacker.UnpackLong();
                LastBlockId = UserDataUnpacker.UnpackLong();
                Split = UserDataUnpacker.UnpackBool();
            }
        }

        public override void Commit()
        {
            UserDataPacker.Position = 0;
            UserDataPacker.Pack(true);
            UserDataPacker.Pack(Version);
            UserDataPacker.Pack(FirstBlockId);
            UserDataPacker.Pack(LastBlockId);
            UserDataPacker.Pack(Split);

            base.Commit();
        }
    }
}
