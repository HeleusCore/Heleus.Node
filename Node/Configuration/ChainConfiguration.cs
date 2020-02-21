using System;
using System.Collections.Generic;
using Heleus.Chain;

namespace Heleus.Node.Configuration
{
    public class ChainConfiguration
    {
        public class ChainKeyInfo
        {
            public readonly ChainKeyStore ChainKeyStore;
            public readonly int AttachementKey;

            public ChainKeyInfo(ChainKeyStore chainKeyStore, int attachementKey)
            {
                ChainKeyStore = chainKeyStore;
                AttachementKey = attachementKey;
            }
        }

        public IReadOnlyList<ChainKeyInfo> Chains;

        public readonly string Service;
        public readonly string ServiceSearchPath;
        public readonly string ServiceConfigString;

        public ChainConfiguration(IReadOnlyList<ChainKeyInfo> chainKeys, string service, string serviceSearchPath, string serviceConfigString)
        {
            Chains = chainKeys;

            Service = service;
            ServiceSearchPath = serviceSearchPath;
            ServiceConfigString = serviceConfigString ?? string.Empty;
        }
    }
}
