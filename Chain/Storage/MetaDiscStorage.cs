using System;
using System.IO;
using Heleus.Base;
using Heleus.Operations;

namespace Heleus.Chain.Storage
{
    public class MetaDiscStorage : DiscStorage
    {
        public long LastBlockId = Protocol.InvalidBlockId;
        public long LastTransactionId = Operation.InvalidTransactionId;

        public MetaDiscStorage(Base.Storage storage, ChainType chainType, int chainId, uint chainIndex, string name, int blockSize, DiscStorageFlags flags, int userDataSize = 20) : base(storage, Path.Combine(Chain.GetChainMetaDirectory(chainType, chainId, chainIndex), name), blockSize, userDataSize, flags)
        {
            if(UserDataUnpacker.UnpackBool())
            {
                LastBlockId = UserDataUnpacker.UnpackLong();
                LastTransactionId = UserDataUnpacker.UnpackLong();
                MetaUnpack();
            }
        }

        protected virtual void MetaUnpack()
        {

        }

        protected virtual void MetaPack()
        {

        }

        public override void Commit()
        {
            UserDataPacker.Position = 0;
            UserDataPacker.Pack(true);
            UserDataPacker.Pack(LastBlockId);
            UserDataPacker.Pack(LastTransactionId);

            MetaPack();

            base.Commit();
        }
    }
}
