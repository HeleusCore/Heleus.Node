using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Chain;
using Heleus.Chain.Core;
using Heleus.Messages;
using Heleus.Network;
using Heleus.ProofOfCouncil;
using Heleus.Transactions;

namespace Heleus.Manager
{
    public class CouncilManager : ILogger
    {
        public class CouncilContainer
        {
            public readonly int ChainId;

            Council _serviceCouncil;
            Council _maintainCouncil;
            Dictionary<uint, Council> _dataCouncils = new Dictionary<uint, Council>();

            public bool IsEmpty => _serviceCouncil == null && _dataCouncils.Count == 0;

            public CouncilContainer(int chainId)
            {
                ChainId = chainId;
            }

            public void SetCouncil(ChainType chainType, uint chainIndex, Council council)
            {
                if (chainType == ChainType.Core || chainType == ChainType.Service)
                {
                    _serviceCouncil = council;
                    return;
                }
                else if (chainType == ChainType.Data)
                {
                    _dataCouncils[chainIndex] = council;
                    return;
                }
                else if (chainType == ChainType.Maintain)
                {
                    _maintainCouncil = council;
                    return;
                }

                throw new ArgumentException("Invalid chain type", nameof(chainType));
            }

            public Council GetCouncil(ChainType chainType, uint chainIndex)
            {
                if (chainType == ChainType.Core || chainType == ChainType.Service)
                {
                    return _serviceCouncil;
                }
                else if (chainType == ChainType.Data)
                {
                    _dataCouncils.TryGetValue(chainIndex, out var council);
                    return council;
                }
                else if (chainType == ChainType.Maintain)
                {
                    return _maintainCouncil;
                }

                throw new ArgumentException("Invalid chain type", nameof(chainType));
            }

            public void Stop()
            {
                _serviceCouncil?.Stop();
                foreach (var council in _dataCouncils.Values)
                    council.Stop();
            }

            public void AddVoteNodeConnection(NodeConnection connection)
            {
                _serviceCouncil?.AddVoteNodeConnection(connection);
                foreach (var council in _dataCouncils.Values)
                    council.AddVoteNodeConnection(connection);
            }

            public void RemoveVoteNodeConnection(NodeConnection connection)
            {
                _serviceCouncil?.RemoveVoteNodeConnection(connection);
                foreach (var council in _dataCouncils.Values)
                    council.RemoveVoteNodeConnection(connection);
            }
        }

        public string LogName => GetType().Name;

        readonly Node.Node _node;
        readonly NodeInfo _nodeInfo;
        readonly CoreChain _coreChain;

        readonly object _lock = new object();

        readonly Dictionary<int, CouncilContainer> _containers = new Dictionary<int, CouncilContainer>();

        public CouncilManager(Node.Node node)
        {
            _node = node;
            _coreChain = node.ChainManager.CoreChain;
            _nodeInfo = _node.NodeConfiguration.LocaleNodeInfo;
        }

        public async Task Start()
        {
            foreach (var nodeKey in _nodeInfo.NodeKeys.Values)
            {
                var chainId = nodeKey.ChainId;
                var chainIndex = nodeKey.ChainIndex;

                if (!_containers.TryGetValue(chainId, out var container))
                {
                    container = new CouncilContainer(chainId);
                    _containers[chainId] = container;
                }

                var key = _node.ChainManager.CoreChain.GetValidPublicChainKeyWithFlags(chainId, chainIndex, nodeKey.KeyIndex, nodeKey.KeyFlags, Time.Timestamp);
                if (key != null && nodeKey.IsSignatureValid(key.PublicKey))
                {
                    if(key.IsServiceChainKey || key.IsCoreChainKey)
                    {
                        if (chainId == Protocol.CoreChainId)
                        {
                            var council = container.GetCouncil(ChainType.Core, chainIndex);
                            if(council == null)
                            {
                                var coreKey = _node.NodeConfiguration.CoreKey;
                                if (coreKey.DecryptedKey.PublicKey == key.PublicKey)
                                {
                                    council = new CoreBlockCouncil(_node, coreKey.KeyIndex, coreKey.DecryptedKey);
                                    container.SetCouncil(ChainType.Core, chainIndex, council);
                                    await council.Start();
                                }
                                else
                                {
                                    Log.Error($"Core key is invalid.", this);
                                }
                            }
                            else
                            {
                                Log.Error($"Container already has a BlockCouncil, skipping key with index {nodeKey.KeyIndex}.", this);
                            }
                        }
                        else
                        {
                            var council = container.GetCouncil(ChainType.Service, chainIndex);
                            if (council == null)
                            {
                                var serviceChain = _node.ChainManager.GetServiceChain(chainId);
                                var maintainChain = _node.ChainManager.GetMaintainChain(chainId);
                                if (serviceChain != null && maintainChain != null)
                                {
                                    var chainKey = serviceChain.KeyStore;
                                    if (chainKey.DecryptedKey.PublicKey == key.PublicKey)
                                    {
                                        council = new ServiceBlockCouncil(_node, chainId, serviceChain, maintainChain, chainKey.KeyIndex, chainKey.DecryptedKey);
                                        container.SetCouncil(ChainType.Service, chainIndex, council);
                                        await council.Start();
                                    }
                                    else
                                    {
                                        Log.Error("Chain key is invalid", this);
                                    }
                                }
                                else
                                {
                                    Log.Error($"Chain container not available for chain {chainId}");
                                }
                            }
                            else
                            {
                                Log.Error($"Container already has a BlockCouncil, skipping key with index {nodeKey.KeyIndex}.", this);
                            }

                            council = container.GetCouncil(ChainType.Maintain, chainIndex);
                            if (council == null)
                            {
                                var maintainChain = _node.ChainManager.GetMaintainChain(chainId);
                                if (maintainChain != null)
                                {
                                    var chainKey = maintainChain.ServiceChain.KeyStore;
                                    if (chainKey.DecryptedKey.PublicKey == key.PublicKey)
                                    {
                                        council = new MaintainBlockCouncil(_node, maintainChain, chainKey.KeyIndex, chainKey.DecryptedKey);
                                        container.SetCouncil(ChainType.Maintain, chainIndex, council);
                                        await council.Start();
                                    }
                                    else
                                    {
                                        Log.Error("Chain key is invalid", this);
                                    }
                                }
                                else
                                {
                                    Log.Error($"Chain container not available for chain {chainId}");
                                }
                            }
                            else
                            {
                                Log.Error($"Container already has a BlockCouncil, skipping key with index {nodeKey.KeyIndex}.", this);
                            }
                        }
                    }

                    if(key.IsDataChainKey)
                    {
                        if (chainId != Protocol.CoreChainId)
                        {
                            var council = container.GetCouncil(ChainType.Data, chainIndex);
                            if(council == null)
                            {
                                var dataChain = _node.ChainManager.GetDataChain(chainId, chainIndex);
                                if (dataChain != null)
                                {
                                    var chainKey = dataChain.KeyStore;
                                    council = new DataBlockCouncil(_node, dataChain, chainKey.KeyIndex, chainKey.DecryptedKey);
                                    container.SetCouncil(ChainType.Data, chainIndex, council);
                                    await council.Start();
                                }
                            }
                            else
                            {
                                Log.Error($"Container already has a DataCoucil, skipping key with index {nodeKey.KeyIndex}.", this);
                            }
                        }
                    }
                }
                else
                {
                    Log.Error($"Node key is invalid ChainId {chainId}, KeyIndex {nodeKey.KeyIndex}", this);
                }

                if (container.IsEmpty)
                {
                    Log.Warn($"Container for chain {container.ChainId} is empty, removing.", this);
                    _containers.Remove(container.ChainId);
                }
            }
        }

        public Task Stop()
        {
            foreach (var container in _containers.Values)
            {
                container.Stop();
            }
            _containers.Clear();

            return Task.CompletedTask;
        }

        public async Task<bool> OnMessage(CouncilMessage message, NodeConnection connection)
        {
            if (_containers.TryGetValue(message.ChainId, out var container))
            {
                var council = container.GetCouncil(message.ChainType, message.ChainIndex);
                if (council == null)
                    return false;

                var key = _coreChain.GetValidPublicChainKeyWithFlags(council.ChainId, council.ChainIndex, council.LocalKeyIndex, council.RequiresChainKeyFlags, Time.Timestamp);
                if (!message.IsValidCouncilMemberSignature(key?.PublicKey))
                    return false;

                await council.OnMessage(message, connection);
            }

            return false;
        }

        public bool HandleNewTransaction(Transaction transaction, TransactionValidation validation, NodeConnection connection)
        {
            if (_containers.TryGetValue(transaction.TargetChainId, out var container))
            {
                var council = container.GetCouncil(transaction.TargetChainType, transaction.ChainIndex);
                if (council != null)
                {
                    council.NewTransaction(transaction, connection);
                    return true;
                }
            }

            return false;
        }

        public bool NewNodeConnection(NodeConnection connection)
        {
            var nodeInfo = connection.NodeInfo;

            lock (_lock)
            {
                foreach (var nodeKey in nodeInfo.NodeKeys.Values)
                {
                    if (_containers.TryGetValue(nodeKey.ChainId, out var container))
                    {
                        container.AddVoteNodeConnection(connection);
                    }
                }
            }

            foreach (var nodeKey in nodeInfo.NodeKeys.Values)
            {
                if (_containers.TryGetValue(nodeKey.ChainId, out var container))
                {
                    var k = _coreChain.GetValidPublicChainKeyWithFlags(nodeKey.ChainId, nodeKey.ChainIndex, nodeKey.KeyIndex, nodeKey.KeyFlags, Time.Timestamp);
                    if (!nodeKey.IsSignatureValid(k?.PublicKey))
                    {
                        Log.Info($"Council key signature invalid for chain {nodeKey.ChainId} and keyindex {nodeKey.KeyFlags} for connection {connection.ConnectionId}.", this);
                        return false;
                    }
                }
            }

            return true;
        }

        public void NodeConnectionClosed(NodeConnection connection)
        {
            lock (_lock)
            {
                foreach (var container in _containers.Values)
                {
                    container.RemoveVoteNodeConnection(connection);
                }
            }
        }
    }
}
