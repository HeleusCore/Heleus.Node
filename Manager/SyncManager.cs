using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Chain;
using Heleus.Chain.Blocks;
using Heleus.Chain.Core;
using Heleus.Chain.Storage;
using Heleus.Network;
using Heleus.Node;
using Heleus.Node.Configuration;

namespace Heleus.Manager
{
    public partial class SyncManager : ILogger
    {
        public string LogName => nameof(SyncManager);

        readonly object _lock = new object();

        readonly PubSub _pubSub;
        readonly ChainManager _chainManager;
        readonly CoreChain _coreChain;
        readonly Storage _storage;
        readonly NodeConfiguration _configuration;

        Dictionary<Tuple<int, uint, ChainType>, ChainSyncItem> _chainSyncItems = new Dictionary<Tuple<int, uint, ChainType>, ChainSyncItem>();
        readonly Host _host;

        ChainSyncItem GetChainSyncItem(int chainId, uint chainIndex, ChainType chainType)
        {
            lock (_lock)
            {
                _chainSyncItems.TryGetValue(new Tuple<int, uint, ChainType>(chainId, chainIndex, chainType), out var chainItem);
                return chainItem;
            }
        }

        public SyncManager(Node.Node node)
        {
            _pubSub = node.PubSub;
            _chainManager = node.ChainManager;
            _coreChain = _chainManager.CoreChain;
            _storage = node.Storage;
            _configuration = node.NodeConfiguration;
            _host = node.Host;
        }

        public async Task<bool> Start()
        {
            Log.Write("Starting sync.", this);

            _chainSyncItems = await GetChainSyncItems();

            foreach (var chainSync in _chainSyncItems.Values)
            {
                var chainType = chainSync.ChainType;
                var chainId = chainSync.ChainId;
                var chainIndex = chainSync.ChainIndex;

                if (chainSync.Remotes.Count == 0)
                {
                    var chain = _chainManager.GetChain(chainType, chainId, chainIndex);
                    if (chain.AvailableEndpoints.Count > 0)
                    {
                        Log.Warn($"No valid sync node for chain {chainId} found.", this);
                    }

                    //chainSync.UpdateLastBlockInfo(_coreChain.GetChainInfo(chainId)?.LastState);
                }

                var failCount = 0;
                var downloaded = true;

            retrySlices:
                if (failCount > 10)
                {
                    Log.Error($"Sync failed, slice download for chain {chainId} failed.", this);
                    return false;
                }

                foreach (var remote in chainSync.Remotes)
                {
                    downloaded = await DownloadTransactionSlices(remote, chainSync);
                    if (downloaded)
                        break;
                }

                if (!downloaded)
                {
                    failCount++;
                    goto retrySlices;
                }

                failCount = 0;
                downloaded = true;

                foreach (var remote in chainSync.Remotes)
                {
                    downloaded = await DownloadBlockSlices(remote, chainSync);
                    if (downloaded)
                        break;
                }

                if (!downloaded)
                {
                    failCount++;
                    goto retrySlices;
                }
            }

            await _chainManager.Start(true);

            foreach (var chainItemSync in _chainSyncItems.Values)
            {
                var chain = chainItemSync.Chain;
                while (chain.LastProcessedBlockId < chain.BlockStorage.LastStoredBlockId)
                {
                    var blockData = await chain.BlockStorage.GetBlockData(chain.LastProcessedBlockId + 1);
                    _chainManager.ConsumeBlockData(blockData);
                }
            }

            foreach (var chainItemSync in _chainSyncItems.Values)
            {
                foreach (var remote in chainItemSync.Remotes)
                {
                    await DownloadBlocks(remote, chainItemSync);
                }
            }

            return true;
        }

        public async Task HandleBlockData(BlockData blockData, HashSet<NodeConnection> nodeConnections)
        {
            if (blockData == null)
                return;

            var block = blockData.Block;
            var chainId = block.ChainId;
            var chainIndex = block.ChainIndex;
            var chainSyncItem = GetChainSyncItem(chainId, chainIndex, block.ChainType);

            if (blockData.ChainType == ChainType.Core)
            {
                if (blockData is BlockData<CoreBlock> coreBlockData)
                {
                    await HandleBlockData(coreBlockData, chainSyncItem, _coreChain, nodeConnections);
                }
            }
            else if (blockData.ChainType == ChainType.Service)
            {
                if (blockData is BlockData<ServiceBlock> serviceBlockData)
                {
                    var chain = _chainManager.GetServiceChain(chainId);
                    if (chain != null)
                        await HandleBlockData(serviceBlockData, chainSyncItem, chain, nodeConnections);
                }
            }
            else if (blockData.ChainType == ChainType.Data)
            {
                if (blockData is BlockData<DataBlock> dataBlockData)
                {
                    var chain = _chainManager.GetDataChain(chainId, chainIndex);
                    if (chain != null)
                        await HandleBlockData(dataBlockData, chainSyncItem, chain, nodeConnections);
                }
            }
            else if (blockData.ChainType == ChainType.Maintain)
            {
                if(blockData is BlockData<MaintainBlock> maintainBlockData)
                {
                    var chain = _chainManager.GetMaintainChain(chainId);
                    if (chain != null)
                        await HandleBlockData(maintainBlockData, chainSyncItem, chain, nodeConnections);
                }
            }
            else
            {
                throw new Exception($"Unkown ChainType {blockData.ChainType}.");
            }
        }

        async Task HandleBlockData(BlockData blockData, ChainSyncItem chainSyncItem, Chain.Chain chain, HashSet<NodeConnection> nodeConnections)
        {
            try
            {
                if (blockData == null)
                    return;

                var block = blockData.Block;
                var blockId = block.BlockId;
                var blockStorage = chain.BlockStorage;
                var lastStoredBlockId = blockStorage.LastStoredBlockId;
                var chainId = block.ChainId;
                var sync = chainSyncItem.Sync;

                if (blockId <= blockStorage.LastStoredBlockId)
                    return;

                lock (sync.Lock)
                {
                    if (sync.NewBlocks.Count > 0)
                    {
                        // remove older blocks
                        var first = sync.NewBlocks.First().Value;
                        while (first != null)
                        {
                            if (first.BlockId <= lastStoredBlockId)
                                sync.Remove(first.BlockId);
                            else
                                break;

                            if (sync.NewBlocks.Count > 0)
                                first = sync.NewBlocks.First().Value;
                            else
                                break;
                        }
                    }
                }

                if (!_coreChain.IsBlockSignatureValid(blockData))
                    return;

                lock (sync.Lock)
                {
                    if (sync.NewBlocks.ContainsKey(blockId))
                    {
                        var data = sync.NewBlocks[blockId];
                        data.UpdateBlockData(blockData);

                        var first = sync.NewBlocks.First().Value;

                        if (first.BlockId != blockId) // if first in the list, let it pass
                        {
                            if (first.BlockData != null)
                                TaskRunner.Run(() => HandleBlockData(first.BlockData, chainSyncItem, chain, nodeConnections));
                            return;
                        }
                    }
                }

                await sync.ConsumeLock.WaitAsync();

                var result = await blockStorage.StoreBlock(blockData);
                if (result == BlockConsumeResult.Ok)
                {
                    Log.Trace($"New block consumed {blockId}, chainid {chainId}/{block.ChainIndex} ({block.ChainType}). Last stored block id {lastStoredBlockId}.", this);
                    _chainManager.ConsumeBlockData(blockData);
                }

                sync.ConsumeLock.Release();

                if (result == BlockConsumeResult.InvalidHash)
                {
                    Log.Warn($"Valid block {blockId} for chain {chainId} with invalid hash received.", this);
                    lock (sync.Lock)
                    {
                        sync.Remove(blockId);
                        return;
                    }
                }

                nodeConnections = nodeConnections ?? new HashSet<NodeConnection>();
                lock (sync.Lock)
                {
                    if (result == BlockConsumeResult.Ok)
                    {
                        var index = sync.IndexOf(blockId);
                        if (index >= 0)
                        {
                            for (var i = 0; i <= index; i++)
                                sync.RemoveAt(0);
                        }

                        if (sync.NewBlocks.Count > 0)
                        {
                            var first = sync.NewBlocks.First().Value;
                            if (first.BlockData != null)
                            {
                                TaskRunner.Run(() => HandleBlockData(first.BlockData, chainSyncItem, chain, nodeConnections));
                            }
                        }

                        return;
                    }

                    if (sync.NewBlocks.ContainsKey(blockId))
                        return;

                    var startBlock = lastStoredBlockId + 1;
                    var endBlock = blockId;

                    if (sync.NewBlocks.Count == 0)
                    {
                        for (var i = startBlock; i <= endBlock; i++)
                        {
                            var download = new SyncDownload(i);
                            if (download.BlockId == endBlock)
                            {
                                download.UpdateBlockData(blockData);
                            }
                            sync.Add(download.BlockId, download);

                            TaskRunner.Run(() => DownloadBlock(download.BlockId, chainSyncItem, nodeConnections));

                            if ((i - startBlock) > 50)
                                break;
                        }
                    }
                    else
                    {
                        var first = sync.NewBlocks.First().Value;
                        var last = sync.NewBlocks.Last().Value;
                        var start = Math.Min(first.BlockId, startBlock);
                        var end = Math.Max(last.BlockId, endBlock);

                        for (var i = start; i <= end; i++)
                        {
                            var added = sync.NewBlocks.TryGetValue(i, out var download);
                            if (download != null && download.TimedOut && download.BlockData == null)
                            {
                                TaskRunner.Run(() => DownloadBlock(download.BlockId, chainSyncItem, nodeConnections));
                            }

                            download = download ?? new SyncDownload(i);
                            if (download.BlockId == endBlock)
                            {
                                download.UpdateBlockData(blockData);
                            }

                            if (!added)
                            {
                                sync.Add(download.BlockId, download);
                            }

                            if ((!added || download.TimedOut) && download.BlockData == null)
                            {
                                TaskRunner.Run(() => DownloadBlock(download.BlockId, chainSyncItem, nodeConnections));
                                if (sync.NewBlocks.Count > 50)
                                    break;
                            }
                        }
                    }

                    if (sync.NewBlocks.Count > 0)
                    {
                        var first = sync.NewBlocks.First().Value;
                        if (first.BlockData != null)
                        {
                            TaskRunner.Run(() => HandleBlockData(first.BlockData, chainSyncItem, chain, nodeConnections));
                        }
                        else if (first.TimedOut)
                        {
                            TaskRunner.Run(() => DownloadBlock(first.BlockId, chainSyncItem, nodeConnections));
                        }
                    }
                }
            }
            catch(Exception ex)
            {
                Log.HandleException(ex, this);
            }
        }
    }
}
