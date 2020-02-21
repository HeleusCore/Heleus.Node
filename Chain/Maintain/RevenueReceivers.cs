using System.Collections.Generic;
using Heleus.Base;
using Heleus.Chain.Core;

namespace Heleus.Chain.Maintain
{
    public class RevenueReceivers : IPackable
    {
        public readonly int Tick;
        public readonly int PreviousTick;
        public ChainRevenueInfo RevenueInfo;

        public readonly HashSet<long> Accounts = new HashSet<long>();

        public RevenueReceivers(int tick, int previousTick, ChainRevenueInfo revenueInfo)
        {
            Tick = tick;
            RevenueInfo = revenueInfo;
            PreviousTick = previousTick;
        }

        public RevenueReceivers(Unpacker unpacker)
        {
            unpacker.Unpack(out Tick);
            unpacker.Unpack(out PreviousTick);
            unpacker.Unpack(Accounts);
            RevenueInfo = new ChainRevenueInfo(unpacker);
        }

        public void Pack(Packer packer)
        {
            packer.Pack(Tick);
            packer.Pack(PreviousTick);
            packer.Pack(Accounts);
            packer.Pack(RevenueInfo);
        }

        public byte[] ToByteArray()
        {
            using (var packer = new Packer())
            {
                Pack(packer);
                return packer.ToByteArray();
            }
        }
    }
}
