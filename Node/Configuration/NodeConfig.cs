using System;
using System.Collections.Generic;
using Heleus.Base;

namespace Heleus.Node.Configuration
{
    public class NodeConfig : Config
    {
        public class NodeInfo
        {
            public string Host = "127.0.0.1";
            public ushort Port = 54321;

            public bool IsPublic;
            public string PublicEndpoint = "https://configure.me";

            public bool EnableClientConnections = true;

            public bool EnableRemoteService;
            public ushort RemoteServicePort = 34321;
        }

        public NodeInfo Node = new NodeInfo();
        public string NodePrivateKey;

        public string NetworkPublicKey;

        public HashSet<string> BeaconNodes = new HashSet<string>();

        public HashSet<string> AutoConnectNodes = new HashSet<string>();
        public HashSet<string> TrustedNodeIds = new HashSet<string>();

        public int MaxIncomingConnections = 20;
        public int MaxOutgoingConnectoins = 40;

        public LogLevels LogLevel = LogLevels.Info;
        public HashSet<string> LogIgnore = new HashSet<string>();

        protected override void Loaded()
        {
            if (BeaconNodes == null)
                BeaconNodes = new HashSet<string>();
            if (BeaconNodes.Count == 0)
                BeaconNodes.Add("https://heleusnode.heleuscore.com/");

            if (AutoConnectNodes == null)
                AutoConnectNodes = new HashSet<string>();
            if (TrustedNodeIds == null)
                TrustedNodeIds = new HashSet<string>();

            if (LogIgnore == null)
                LogIgnore = new HashSet<string>();
            if (LogIgnore.Count == 0)
                LogIgnore.Add(string.Empty);
        }
    }
}
