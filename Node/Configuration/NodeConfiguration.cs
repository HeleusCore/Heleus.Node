using System;
using Heleus.Base;
using Heleus.Cryptography;
using Heleus.Network;
using System.Collections.Generic;
using Heleus.Chain;

namespace Heleus.Node.Configuration
{
    public class NodeConfiguration
    {
        public readonly Key NetworkPublicKey;

        public readonly Key LocaleNodePrivateKey;
        public readonly NodeInfo LocaleNodeInfo;

        public readonly bool EnableClientConnections;

        public readonly IReadOnlyList<Uri> BeaconNodes;

        public readonly IReadOnlyList<Uri> AutoConnectNodes;
        public readonly IReadOnlyList<Hash> TrustedNodeIds;

        public readonly ChainKeyStore CoreKey;
        public readonly IReadOnlyList<ChainConfiguration> ChainConfigurations;

        public NodeConfiguration(NodeConfig nodeConfig, CoreKeyConfig coreKeyConfig, ChainConfig chainConfig)
        {
            try
            {
                if (!string.IsNullOrEmpty(nodeConfig.NodePrivateKey))
                {
                    LocaleNodePrivateKey = Key.Restore(nodeConfig.NodePrivateKey);

                    if (LocaleNodePrivateKey.KeyType != Protocol.MessageKeyType)
                    {
                        Log.Warn("Stored node key has the wrong key type.");
                        LocaleNodePrivateKey = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Stored node key is invalid.");
                Log.IgnoreException(ex);
            }

            if (LocaleNodePrivateKey == null)
            {
                Log.Trace("Generating new node key.");
                LocaleNodePrivateKey = Key.Generate(Protocol.MessageKeyType);
                nodeConfig.NodePrivateKey = LocaleNodePrivateKey.HexString;
                Config.Save(nodeConfig);
            }

            var beacons = new List<Uri>();
            foreach (var beacon in nodeConfig.BeaconNodes)
            {
                if (!beacon.IsNullOrEmpty())
                    beacons.Add(new Uri(beacon));
            }
            BeaconNodes = beacons;

            var trusted = new List<Hash>();
            foreach (var nodeId in nodeConfig.TrustedNodeIds)
                trusted.Add(Hash.Restore(nodeId));
            TrustedNodeIds = trusted;

            var autoConnect = new List<Uri>();
            foreach (var endPoint in nodeConfig.AutoConnectNodes)
                autoConnect.Add(new Uri(endPoint));
            AutoConnectNodes = autoConnect;

            var keyCollector = new NodeInfoKeyCollector();
            if (coreKeyConfig != null)
            {
                try
                {
                    var keyStore = KeyStore.Restore<ChainKeyStore>(coreKeyConfig.Key);
                    keyStore.DecryptKeyAsync(coreKeyConfig.Password, true).Wait();

                    CoreKey = keyStore;

                    var keyFlags = keyStore.Flags;
                    keyCollector.AddNodeInfoKey(Protocol.CoreChainId, 0, keyStore.KeyIndex, keyFlags, keyStore.DecryptedKey);
                }
                catch (Exception ex)
                {
                    Log.Error("Core vote key or password invalid.");
                    Log.IgnoreException(ex);
                }
            }

            var chains = new List<ChainConfiguration>();
            if (chainConfig != null && chainConfig.Chains.Count > 0)
            {
                try
                {
                    foreach (var sc in chainConfig.Chains)
                    {
                        var chainKeys = new List<ChainConfiguration.ChainKeyInfo>();
                        foreach(var sk in sc.ChainKeys)
                        {
                            var chainKey = KeyStore.Restore<ChainKeyStore>(sk.ChainKey);
                            try
                            {
                                chainKey.DecryptKeyAsync(sk.ChainKeyPassword, true).Wait();
                            }
                            catch(Exception ex)
                            {
                                Log.Error($"Invalid chain key {chainKey.ChainId}/{chainKey.ChainIndex} password ({sc.Service})");
                                throw ex;
                            }

                            chainKeys.Add(new ChainConfiguration.ChainKeyInfo(chainKey, sk.AttachementKey));

                            var chainId = chainKey.ChainId;
                            if (chainKey.ChainId > Protocol.CoreChainId)
                            {
                                var keyFlags = chainKey.Flags;
                                keyCollector.AddNodeInfoKey(chainKey.ChainId, chainKey.ChainIndex, chainKey.KeyIndex, keyFlags, chainKey.DecryptedKey);
                            }
                        }

                        chains.Add(new ChainConfiguration(chainKeys, sc.Service, sc.ServiceSearchPath, sc.ServiceConfig));
                    }

                }
                catch (Exception ex)
                {
                    Log.Error($"Chain config key or password is invalid.");
                    Log.IgnoreException(ex);
                }
            }

            ChainConfigurations = chains;

            EnableClientConnections = nodeConfig.Node.EnableClientConnections;
            if (!nodeConfig.NetworkPublicKey.IsNullOrWhiteSpace())
            {
                NetworkPublicKey = Key.Restore(nodeConfig.NetworkPublicKey);
                Uri publicEndPoint = null;
                if (nodeConfig.Node.IsPublic && !string.IsNullOrWhiteSpace(nodeConfig.Node.PublicEndpoint))
                    publicEndPoint = new Uri(nodeConfig.Node.PublicEndpoint);

                LocaleNodeInfo = new NodeInfo(NetworkPublicKey, LocaleNodePrivateKey, publicEndPoint, keyCollector);
            }
        }
    }
}
