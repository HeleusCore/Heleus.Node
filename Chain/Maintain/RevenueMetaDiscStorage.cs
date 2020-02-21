using Heleus.Base;
using Heleus.Chain.Storage;

namespace Heleus.Chain.Maintain
{
    public class RevenueMetaDiscStorage : MetaDiscStorage
    {
        public int LastAvailableTick = -1;
        public int LastProcessedTick = -1;

        public RevenueMetaDiscStorage(Base.Storage storage, ChainType chainType, int chainId, uint chainIndex, string name) : base(storage, chainType, chainId, chainIndex, name, 1024, DiscStorageFlags.UnsortedDynamicIndex | DiscStorageFlags.AppendOnly, 64)
        {
        }

        protected override void MetaUnpack()
        {
            base.MetaUnpack();
            LastAvailableTick = UserDataUnpacker.UnpackInt();
            LastProcessedTick = UserDataUnpacker.UnpackInt();
        }

        protected override void MetaPack()
        {
            base.MetaPack();
            UserDataPacker.Pack(LastAvailableTick);
            UserDataPacker.Pack(LastProcessedTick);
        }
    }
}
