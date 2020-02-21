using System.Collections.Generic;
using Heleus.Base;

namespace Heleus.Node.Configuration
{
    public class ChainConfig : Config
    {
        public class ChainKeyInfo
        {
            public string ChainKey;
            public string ChainKeyPassword;
            public int AttachementKey;
        }

        public class ChainInfo
        {
            public List<ChainKeyInfo> ChainKeys;

            public string Service = string.Empty;
            public string ServiceSearchPath = string.Empty;
            public string ServiceConfig = string.Empty;
        }

        public HashSet<ChainInfo> Chains = new HashSet<ChainInfo>();

        protected override void Loaded()
        {
            if (Chains == null)
                Chains = new HashSet<ChainInfo>();

            foreach(var chain in Chains)
            {
                if (chain.ChainKeys == null)
                    chain.ChainKeys = new List<ChainKeyInfo>();
            }
        }
    }
}
