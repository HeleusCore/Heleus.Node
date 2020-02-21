using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Chain.Core;
using Heleus.Transactions;
using Heleus.Chain.Blocks;
using Heleus.Chain.Service;
using Heleus.Chain;
using Heleus.Chain.Data;
using Heleus.Service;
using Heleus.Chain.Maintain;

namespace Heleus.Manager
{
    public class ChainManager : ILogger
    {
        public readonly CoreChain CoreChain;

        public string LogName => GetType().Name;

        public IReadOnlyDictionary<int, ServiceChain> ServiceChains { get; private set; }
        public IReadOnlyDictionary<(int, uint), DataChain> DataChains { get; private set; }
        public IReadOnlyDictionary<int, MaintainChain> MaintainChains { get; private set; }
        public IReadOnlyDictionary<int, ServiceHost> ServiceHosts { get; private set; }
        public IReadOnlyDictionary<int, ChainContainer> ChainContainers { get; private set; }

        readonly List<IServiceBlockHandler> _serviceBlockReceivers = new List<IServiceBlockHandler>();

        readonly Node.Node _node;
        readonly PubSub _pubSub;

        public ChainManager(Node.Node node)
        {
            Transaction.Init();

            _pubSub = node.PubSub;
            _node = node;

            CoreChain = new CoreChain(node);
        }

        public async Task<bool> Initalize()
        {
            await CoreChain.Initalize();

            var serviceChains = new Dictionary<int, ServiceChain>();
            var dataChains = new Dictionary<(int, uint), DataChain>();
            var maintainChains = new Dictionary<int, MaintainChain>();
            var serviceHosts = new Dictionary<int, ServiceHost>();
            var containers = new Dictionary<int, ChainContainer>();

            foreach (var config in _node.NodeConfiguration.ChainConfigurations)
            {
                ChainKeyStore serviceKey = null;
                ChainKeyStore serviceVoteKey = null;
                var chainId = -1;

                if(config.Chains.Count == 0)
                {
                    Log.Error($"No chain keys available for {config.Service}.", this);
                    return false;
                }

                foreach(var chain in config.Chains)
                {
                    var chainKey = chain.ChainKeyStore;

                    if (chainId == -1)
                    {
                        chainId = chainKey.ChainId;
                        if(serviceChains.ContainsKey(chainId))
                        {
                            Log.Error($"Chain with id {chainId} already added ({config.Service}).", this);
                            return false;
                        }
                    }
                    else
                    {
                        if (chainId != chainKey.ChainId)
                        {
                            Log.Error("$", this);
                            return false;
                        }
                    }

                    var chainInfo = CoreChain.GetChainInfo(chainId, true);
                    if (chainInfo != null)
                    {
                        var chainInfoKey = chainInfo.GetChainKey(chainKey.KeyIndex);
                        if(chainInfoKey == null || chainInfoKey.PublicKey != chainKey.PublicKey)
                            Log.Warn($"Chain key for id {chainId}/{chainKey.ChainIndex} with index {chainKey.KeyIndex} seems to be invalid ({config.Service}).", this);
                    }

                    var publicChainKey = chainKey.PublicChainKey;
                    if(publicChainKey.IsServiceChainKey)
                    {
                        serviceKey = chainKey;
                    }

                    if(publicChainKey.IsServcieChainVoteKey)
                    {
                        if(serviceVoteKey != null)
                            Log.Warn($"More then one service vote key availablef for chain {chainId}/{chainKey.ChainIndex}, using key index {serviceVoteKey.KeyIndex} ({config.Service}).", this);

                        serviceVoteKey = chainKey;
                    }
                }

                if(serviceKey == null)
                {
                    Log.Error($"No service key for chain {chainId} found ({config.Service}).", this);
                    return false;
                }

                serviceKey = serviceVoteKey ?? serviceKey;

                Log.Info($"Running service chain {chainId} with public key {serviceKey.DecryptedKey.PublicKey.HexString} ({config.Service}).", this);

                if(chainId == Protocol.CoreChainId)
                {
                    var sh = new ServiceHost(_node, null, new Dictionary<uint, DataChain>(), null, _node.Storage, Protocol.CoreChainId, config.Service, config.ServiceSearchPath, config.ServiceConfigString);
                    serviceHosts[chainId] = sh;
                    continue;
                }

                var serviceChain = new ServiceChain(chainId, _node, CoreChain, serviceKey);
                serviceChains[chainId] = serviceChain;

                var maintainChain = new MaintainChain(serviceChain, _node);
                maintainChains[chainId] = maintainChain;

                var dc = new Dictionary<uint, DataChain>();

                foreach(var chain in config.Chains)
                {
                    var chainKey = chain.ChainKeyStore;
                    var publicChainKey = chainKey.PublicChainKey;

                    if(publicChainKey.IsDataChainKey)
                    {
                        var chainIndex = publicChainKey.ChainIndex;
                        if (dc.ContainsKey(chainIndex))
                        {
                            Log.Error($"Data chain with id {chainId}/{chainIndex} already added ({config.Service}).", this);
                            return false;
                        }

                        Log.Info($"Running data chain {chainId}/{chainIndex} with public key {publicChainKey.PublicKey.HexString}.", this);

                        var dataChain = new DataChain(chainId, chainIndex, _node, CoreChain, serviceChain, chainKey, chain.AttachementKey);
                        if (chain.AttachementKey > -1)
                            _node.AttachementManager.AddChainAttachementsCache(dataChain);

                        dc[chainIndex] = dataChain;
                        dataChains[(chainId, chainIndex)] = dataChain;
                    }
                }

                var serviceHost = new ServiceHost(_node, serviceChain, dc, maintainChain, _node.Storage, chainId, config.Service, config.ServiceSearchPath, config.ServiceConfigString);
                serviceHosts[chainId] = serviceHost;

                var container = new ChainContainer(serviceChain, dc, maintainChain, serviceHost);
                containers[chainId] = container;
            }

            ServiceChains = serviceChains;
            DataChains = dataChains;
            MaintainChains = maintainChains;
            ServiceHosts = serviceHosts;
            ChainContainers = containers;

            foreach (var serviceChain in ServiceChains.Values)
                await serviceChain.Initalize();

            foreach (var maintainChain in MaintainChains.Values)
                await maintainChain.Initalize();

            foreach (var dataChain in DataChains.Values)
                await dataChain.Initalize();

            return true;
        }

        public async Task Start(bool startAllChains)
        {
            await Stop();
            await CoreChain.Start();

            if (startAllChains)
            {
                foreach (var chain in ServiceChains.Values)
                    await chain.Start();

                foreach (var chain in MaintainChains.Values)
                    await chain.Start();

                foreach (var chain in DataChains.Values)
                    await chain.Start();

                foreach (var serviceHost in ServiceHosts.Values)
                {
                    await serviceHost.Start();
                    if (serviceHost.BlockReceiver != null)
                        _serviceBlockReceivers.Add(serviceHost.BlockReceiver);
                }
            }
        }

        public async Task Stop()
        {
            foreach (var service in ServiceHosts.Values)
                await service.Stop();
            _serviceBlockReceivers.Clear();

            foreach (var chain in DataChains.Values)
                await chain.Stop();

            foreach (var chain in MaintainChains.Values)
                await chain.Stop();

            foreach (var chain in ServiceChains.Values)
                await chain.Stop();

            await CoreChain.Stop();
        }

        public ServiceChain GetServiceChain(int chainId)
        {
            ServiceChains.TryGetValue(chainId, out var chain);
            return chain;
        }

        public DataChain GetDataChain(int chainId, uint chainIndex)
        {
            if (DataChains.TryGetValue((chainId, chainIndex), out var chain))
                return chain;

            return null;
        }

        public MaintainChain GetMaintainChain(int chainId)
        {
            MaintainChains.TryGetValue(chainId, out var chain);
            return chain;
        }

        public Chain.Chain GetChain(ChainType chainType, int chainId, uint chainIndex)
        {
            if (chainId == CoreChain.CoreChainId && chainType == ChainType.Core)
                return CoreChain;

            if (chainType == ChainType.Service)
                return GetServiceChain(chainId);
            if (chainType == ChainType.Data)
                return GetDataChain(chainId, chainIndex);
            if (chainType == ChainType.Maintain)
                return GetMaintainChain(chainId);

            throw new Exception($"ChainType {chainType} not found.");
        }

        public IService GetService(int chainId)
        {
            ServiceHosts.TryGetValue(chainId, out var serviceHost);
            return serviceHost?.Service;
        }

        public ServiceHost GetServiceHost(int chainId)
        {
            ServiceHosts.TryGetValue(chainId, out var serviceHost);
            return serviceHost;
        }

        public ChainContainer GetContainer(int chainId)
        {
            ChainContainers.TryGetValue(chainId, out var container);
            return container;
        }

        public void ConsumeBlockData(BlockData blockData)
        {
            if (blockData == null)
                return;

            if (blockData.ChainType == ChainType.Core)
            {
                if (blockData is BlockData<CoreBlock> coreBlockData)
                {
                    ConsumeBlockData(coreBlockData);
                    TaskRunner.Run(() => _pubSub.PublishAsync(new BlockEvent<CoreBlock>(coreBlockData)));
                }
            }
            else if (blockData.ChainType == ChainType.Service)
            {
                if (blockData is BlockData<ServiceBlock> serviceBlockData)
                {
                    ConsumeBlockData(serviceBlockData);
                    TaskRunner.Run(() => _pubSub.PublishAsync(new BlockEvent<ServiceBlock>(serviceBlockData)));
                }
            }
            else if (blockData.ChainType == ChainType.Data)
            {
                if (blockData is BlockData<DataBlock> dataBlockData)
                {
                    ConsumeBlockData(dataBlockData);
                    TaskRunner.Run(() => _pubSub.PublishAsync(new BlockEvent<DataBlock>(dataBlockData)));
                }
            }
            else if (blockData.ChainType == ChainType.Maintain)
            {
                if(blockData is BlockData<MaintainBlock> maintainBlockData)
                {
                    ConsumeBlockData(maintainBlockData);
                    TaskRunner.Run(() => _pubSub.PublishAsync(new BlockEvent<MaintainBlock>(maintainBlockData)));
                }
            }
            else
            {
                throw new Exception($"Invalid ChainType {blockData.ChainType}.");
            }
        }

        void ConsumeBlockData(BlockData<CoreBlock> blockData)
        {
            CoreChain.ConsumeCoreBlockData(blockData);

            foreach (var blockReceiver in _serviceBlockReceivers)
            {
                TaskRunner.Run(() => blockReceiver.NewBlockData(blockData));
            }
        }

        void ConsumeBlockData(BlockData<ServiceBlock> blockData)
        {
            var block = blockData?.Block;
            if (block != null)
            {
                var chainId = block.ChainId;
                var serviceChain = GetServiceChain(chainId);
                if (serviceChain != null)
                {
                    serviceChain.ConsumeBlockData(blockData);

                    if (ServiceHosts.TryGetValue(chainId, out var serviceHost) && serviceHost.BlockReceiver != null)
                    {
                        TaskRunner.Run(() => serviceHost.BlockReceiver.NewBlockData(blockData));
                    }
                }
            }
        }

        void ConsumeBlockData(BlockData<DataBlock> blockData)
        {
            var block = blockData?.Block;
            if (block != null)
            {
                var chainId = block.ChainId;
                var dataChain = GetDataChain(chainId, block.ChainIndex);
                if (dataChain != null)
                {
                    dataChain.ConsumeBlockData(blockData);

                    if (ServiceHosts.TryGetValue(chainId, out var serviceHost) && serviceHost.BlockReceiver != null)
                    {
                        TaskRunner.Run(serviceHost.BlockReceiver.NewBlockData(blockData));
                    }
                }
            }
        }

        void ConsumeBlockData(BlockData<MaintainBlock> blockData)
        {
            var block = blockData?.Block;
            if(block != null)
            {
                var maintainChain = GetMaintainChain(block.ChainId);
                maintainChain.ConsumeBlockData(blockData);
            }
        }
    }
}
