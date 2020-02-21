using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Heleus.Chain;
using Heleus.Chain.Blocks;
using Heleus.Cryptography;
using Heleus.Network.Client;

namespace Heleus.Manager
{
    public partial class SyncManager
    {
        public class SyncDownload
        {
            public readonly long BlockId;
            public bool Downloading;

            public DateTime DownloadTime;
            public BlockData BlockData { get; private set; }

            public SyncDownload(long blockId)
            {
                BlockId = blockId;
                DownloadTime = DateTime.UtcNow;
            }

            public SyncDownload UpdateBlockData(BlockData blockData)
            {
                BlockData = blockData;
                return this;
            }

            public bool TimedOut
            {
                get
                {
                    return DateTime.UtcNow > (DownloadTime + TimeSpan.FromSeconds(5));
                }
            }
        }

        public class SyncObject
        {
            public void Add(long blockId, SyncDownload syncDownload)
            {
                (NewBlocks as SortedList<long, SyncDownload>).Add(blockId, syncDownload);
            }

            public void Remove(long blockId)
            {
                (NewBlocks as SortedList<long, SyncDownload>).Remove(blockId);
            }

            public void RemoveAt(int index)
            {
                (NewBlocks as SortedList<long, SyncDownload>).RemoveAt(index);
            }

            public int IndexOf(long blockId)
            {
                return (NewBlocks as SortedList<long, SyncDownload>).IndexOfKey(blockId);
            }

            public readonly IReadOnlyDictionary<long, SyncDownload> NewBlocks = new SortedList<long, SyncDownload>();

            public readonly object Lock = new object();
            public readonly SemaphoreSlim ConsumeLock = new SemaphoreSlim(1);
            public readonly SemaphoreSlim DownloadLock = new SemaphoreSlim(10);
        }

        class RemoteSyncItem
        {
            public readonly NodeClient Client;
            public readonly LastBlockInfo LastBlockInfo;

            public RemoteSyncItem(NodeClient client, LastBlockInfo blockState)
            {
                Client = client;
                LastBlockInfo = blockState;
            }
        }

        class ChainSyncItem
        {
            public readonly Chain.Chain Chain;
            public ChainType ChainType => Chain.ChainType;
            public int ChainId => Chain.ChainId;
            public uint ChainIndex => Chain.ChainIndex;

            public readonly SyncObject Sync = new SyncObject();

            public LastBlockInfo BlockState { get; private set; }
            public readonly List<RemoteSyncItem> Remotes = new List<RemoteSyncItem>();

            public long LowestFoundBlockSliceId = long.MaxValue;
            public long LowestFoundTransactionSliceId = long.MaxValue;

            public ChainSyncItem(Chain.Chain chain)
            {
                Chain = chain;
            }

            public void AddRemote(RemoteSyncItem remote)
            {
                if (remote.LastBlockInfo == null)
                    return;

                Remotes.Add(remote);
                UpdateLastBlockInfo(remote.LastBlockInfo);
            }

            public void UpdateLastBlockInfo(LastBlockInfo blockState)
            {
                if (blockState == null)
                    return;

                if (BlockState == null)
                {
                    BlockState = blockState;
                    return;
                }

                if (blockState.LastBlockId > BlockState.LastBlockId)
                    BlockState = blockState;
            }

            public void UpdateLowestFoundBlockSliceId(long id)
            {
                if (id < 0)
                    return;

                LowestFoundBlockSliceId = Math.Min(id, LowestFoundBlockSliceId);
            }

            public void UpdateLowestFoundTransactionSliceId(long id)
            {
                if (id < 0)
                    return;

                LowestFoundTransactionSliceId = Math.Min(id, LowestFoundTransactionSliceId);
            }
        }

        async Task<Dictionary<Tuple<int, uint, ChainType>, ChainSyncItem>> GetChainSyncItems()
        {
            var chains = new List<Chain.Chain> { _chainManager.CoreChain };
            chains.AddRange(_chainManager.ServiceChains.Values);
            chains.AddRange(_chainManager.DataChains.Values);
            chains.AddRange(_chainManager.MaintainChains.Values);

            var chainSyncs = new Dictionary<Tuple<int, uint, ChainType>, ChainSyncItem>();
            var remotes = new List<RemoteSyncItem>();

            foreach (var chain in chains)
            {
                var chainType = chain.ChainType;
                var chainId = chain.ChainId;
                var chainIndex = chain.ChainIndex;

                var chainSync = new ChainSyncItem(chain);
                chainSyncs.Add(new Tuple<int, uint, ChainType>(chainId, chainIndex, chainType), chainSync);

                Chain.Chain.CreateRequiredDirectories(_storage, chain.ChainType, chainId, chainIndex, true);

                var endPoints = chain.AvailableEndpoints;
                foreach (var endPoint in endPoints)
                {
                    var client = new NodeClient(endPoint.EndPoint);
                    var nodeInfo = endPoint.NodeInfo;
                    if (nodeInfo == null)
                        nodeInfo = (await client.DownloadNodeInfo(_configuration.NetworkPublicKey)).Data;

                    if (nodeInfo != null && !(nodeInfo.NodeId == _configuration.LocaleNodeInfo.NodeId))
                    {
                        if (endPoint.NodeInfo == null)
                            endPoint.NodeInfo = nodeInfo;

                        var lastBlockInfo = (await client.DownloadLastBlockInfo(chainType, chainId, chainIndex)).Data;
                        if (lastBlockInfo != null)
                        {
                            var blockSliceInfo = (await client.DownloadBlockStorageSliceInfo(chainType, chainId, chainIndex)).Data;
                            var transactionSliceInfo = (await client.DownloadTransactionStorageSliceInfo(chainType, chainId, chainIndex)).Data;

                            var remote = new RemoteSyncItem(client, lastBlockInfo);
                            chainSync.AddRemote(remote);

                            if (blockSliceInfo != null)
                                chainSync.UpdateLowestFoundBlockSliceId(blockSliceInfo.FirstStoredSliceId);
                            if (transactionSliceInfo != null)
                                chainSync.UpdateLowestFoundTransactionSliceId(transactionSliceInfo.FirstStoredSliceId);

                            remotes.Add(remote);
                        }
                    }
                }
            }

            // get latest chain info
            foreach (var chain in chainSyncs.Values)
            {
                var chainType = chain.ChainType;
                var chainId = chain.ChainId;
                var chainIndex = chain.ChainIndex;

                foreach (var remote in chain.Remotes)
                {
                    var lastBlockInfo = (await remote.Client.DownloadLastBlockInfo(chainType, chainId, chainIndex)).Data;
                    chain.UpdateLastBlockInfo(lastBlockInfo);
                }
            }

            return chainSyncs;
        }
    }
}
