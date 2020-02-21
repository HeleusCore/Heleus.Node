using System.Collections.Generic;
using Heleus.Chain.Service;
using Heleus.Chain.Data;
using Heleus.Service;
using Heleus.Chain.Maintain;

namespace Heleus.Manager
{
    public class ChainContainer
    {
        public readonly ServiceChain ServiceChain;
        readonly Dictionary<uint, DataChain> DataChains;
        public readonly MaintainChain MaintainChain;
        public readonly ServiceHost ServiceHost;
        public IService Service => ServiceHost?.Service;

        public ChainContainer(ServiceChain serviceChain, Dictionary<uint, DataChain> dataChains, MaintainChain maintainChain, ServiceHost serviceHost)
        {
            ServiceChain = serviceChain;
            DataChains = dataChains;
            MaintainChain = maintainChain;
            ServiceHost = serviceHost;
        }

        public DataChain GetDataChain(uint chainIndex)
        {
            DataChains.TryGetValue(chainIndex, out var dataChain);
            return dataChain;
        }
    }
}
