using System.Collections.Generic;

namespace Heleus.Chain.Core
{
    public class DirtyData
    {
        public readonly HashSet<long> DirtyAccounts = new HashSet<long>();
        public readonly HashSet<int> DirtyChains = new HashSet<int>();
        public readonly HashSet<int> DirtyRevenueTicks = new HashSet<int>();
    }
}
