using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Chain.Storage;
using Heleus.Messages;
using Heleus.Network;
using Heleus.Network.Client;

namespace Heleus.Manager
{
    public partial class SyncManager
    {
        async Task<bool> DownloadBlocks(RemoteSyncItem remote, ChainSyncItem chainSyncItem)
        {
            var chainType = chainSyncItem.ChainType;
            var chainId = chainSyncItem.ChainId;
            var chainIndex = chainSyncItem.ChainIndex;

            chainSyncItem.UpdateLastBlockInfo((await remote.Client.DownloadLastBlockInfo(chainType, chainId, chainIndex)).Data);

            var chain = _chainManager.GetChain(chainSyncItem.ChainType, chainId, chainIndex);
            var lastChainBlockId = chainSyncItem.BlockState.LastBlockId;

            var lastBlock = chain.BlockStorage.LastBlock;
            var nextBlockId = Protocol.GenesisBlockId;
            if (lastBlock != null)
                nextBlockId = lastBlock.BlockId + 1;

            var sync = chainSyncItem.Sync;
            var newBlocks = chainSyncItem.Sync.NewBlocks;
            var lockitem = chainSyncItem.Sync.Lock;

            var count = 0;
            for (var blockId = nextBlockId; blockId <= lastChainBlockId; blockId++)
            {
                Log.Trace($"Downloading block {blockId}/{lastChainBlockId} for chain {chainId}/{chainIndex}.", this);

                var blockData = (await remote.Client.DownloadBlockData(chainType, chainId, chainIndex, blockId)).Data;
                if (blockData == null)
                {
                    Log.Trace($"Block {blockId} download failed.", this);
                    return false;
                }

                //await HandleBlock(blockData, null);
                lock (lockitem)
                {
                    if (newBlocks.TryGetValue(blockId, out var dl))
                    {
                        if (dl.BlockData == null)
                            dl.UpdateBlockData(blockData);
                    }
                    else
                    {
                        sync.Add(blockId, new SyncDownload(blockId).UpdateBlockData(blockData));
                    }
                }
                count++;
            }

            if (newBlocks.ContainsKey(nextBlockId))
            {
                var last = chainSyncItem.Sync.NewBlocks[nextBlockId];
                await HandleBlockData(last.BlockData, null);
            }

            if (count > 0)
                return await DownloadBlocks(remote, chainSyncItem);

            return true;
        }

        async Task<bool> DownloadBlockSlices(RemoteSyncItem remote, ChainSyncItem chainSyncItem)
        {
            var chainType = chainSyncItem.ChainType;
            var chainId = chainSyncItem.ChainId;
            var chainIndex = chainSyncItem.ChainIndex;

            chainSyncItem.UpdateLastBlockInfo((await remote.Client.DownloadLastBlockInfo(chainType, chainId, chainIndex)).Data);

            var lastBlockId = chainSyncItem.BlockState.LastBlockId;
            if (lastBlockId <= Protocol.InvalidBlockId)
            {
                Log.Debug($"Skipping block slice download for chain {chainId}/{chainIndex}, blockid invalid.", this);
                return true;
            }

            var remoteLastAvailableSlice = BlockSliceInfo.GetSliceIndex(lastBlockId) - 1;
            if (remoteLastAvailableSlice < 0) // -1: ignore last "hot" slice
            {
                Log.Debug($"Skipping block slice download for chain {chainId}/{chainIndex}, no available remote stored slice found.", this);
                return true;
            }

            var localStartSlice = 0L;
            var localLast = BlockStorage.GetBlockSlices(_storage, chainType, chainId, chainIndex, true).LastOrDefault();
            if (localLast.Value != null)
                localStartSlice = localLast.Value.SliceIndex + 1;

            if (!(chainSyncItem.LowestFoundBlockSliceId <= localStartSlice))
            {
                Log.Error($"Download block slices for chain {chainId}/{chainIndex} failed, last local slice is {localStartSlice}, lowest remote slice found is {chainSyncItem.LowestFoundBlockSliceId}.", this);
                return false;
            }

            var count = 0;
            for (var sliceIndex = localStartSlice; sliceIndex <= remoteLastAvailableSlice; sliceIndex++)
            {
                Log.Trace($"Downloading block slice {sliceIndex}/{remoteLastAvailableSlice} for chain {chainId}/{chainIndex}.", this);
                var sliceData = (await remote.Client.DownloadBlockSlice(_storage, chainType, chainId, chainIndex, sliceIndex)).Data;

                var error = false;
                if (sliceData == null || !sliceData.Move())
                {
                    Log.Trace($"Block slice {sliceIndex} download failed.", this);
                    sliceData?.Delete();
                    error = true;
                }

                sliceData?.Dispose();

                if (error)
                    return false;

                count++;
            }

            if (count > 0)
                return await DownloadBlockSlices(remote, chainSyncItem);

            return true;
        }

        async Task<bool> DownloadTransactionSlices(RemoteSyncItem remote, ChainSyncItem chainSyncItem)
        {
            var chainId = chainSyncItem.ChainId;
            var chainType = chainSyncItem.ChainType;
            var chainIndex = chainSyncItem.ChainIndex;

            chainSyncItem.UpdateLastBlockInfo((await remote.Client.DownloadLastBlockInfo(chainType, chainId, chainIndex)).Data);

            var localStartSlice = 0;
            var localLast = TransactionStorage.GetTransactionSlices(_storage, chainType, chainId, chainIndex, true).LastOrDefault();
            if (localLast.Value != null)
                localStartSlice = localLast.Value.SliceId + 1;

            var remoteLastAvailableSlice = TransactionSliceInfo.GetSliceIndex(chainSyncItem.BlockState.LastTransactionId) - 1;
            if (remoteLastAvailableSlice < 0) // -1: ignore last "hot" slice
            {
                Log.Debug($"Skipping transaction slice download for chain {chainId}/{chainIndex}, no available remote stored slice found.", this);
                return true;
            }

            var count = 0;
            if (remoteLastAvailableSlice >= 0 && remoteLastAvailableSlice >= localStartSlice)
            {
                for (var sliceId = localStartSlice; sliceId <= remoteLastAvailableSlice; sliceId++)
                {
                    Log.Trace($"Downloading transaction slice {sliceId}/{remoteLastAvailableSlice} for chain {chainId}.", this);
                    var sliceData = (await remote.Client.DownloadTransactionSlice(_storage, chainType, chainId, chainIndex, sliceId)).Data;

                    var error = false;
                    if (sliceData == null || !sliceData.Move())
                    {
                        Log.Trace($"Slice {sliceId} download for chain {chainId}/{chainIndex} failed.", this);
                        sliceData?.Delete();
                        error = true;
                    }

                    sliceData?.Dispose();

                    if (error)
                        return false;

                    count++;
                }
            }

            if (count > 0) // check for new slices
                return await DownloadTransactionSlices(remote, chainSyncItem);

            return true;
        }

        async Task DownloadBlock(long blockId, ChainSyncItem chainSyncItem, HashSet<NodeConnection> nodeConnections, int tries = 0)
        {
            var retry = false;

            var sync = chainSyncItem.Sync;
            try
            {
                await sync.DownloadLock.WaitAsync();


                SyncDownload download;
                lock (sync.Lock)
                {
                    sync.NewBlocks.TryGetValue(blockId, out download);
                    if (download == null)
                        return;

                    if (!download.TimedOut && download.Downloading)
                        return;

                    download.Downloading = true;
                    download.DownloadTime = DateTime.UtcNow;
                }

                nodeConnections = nodeConnections ?? new HashSet<NodeConnection>();

                foreach (var connection in nodeConnections)
                {
                    var nodeInfo = connection.NodeInfo;
                    if (nodeInfo.IsPublicEndPoint && nodeInfo.NodeId != _configuration.LocaleNodeInfo.NodeId)
                    {
                        var client = new NodeClient(nodeInfo.PublicEndPoint);
                        var blockData = (await client.DownloadBlockData(chainSyncItem.ChainType, chainSyncItem.ChainId, chainSyncItem.ChainIndex, blockId)).Data;
                        if (blockData != null)
                        {
                            download.UpdateBlockData(blockData);
                            await HandleBlockData(blockData, nodeConnections);
                            goto end;
                        }
                        else
                        {
                            retry = true;
                        }
                    }
                }

                foreach (var endPoint in chainSyncItem.Chain.AvailableEndpoints)
                {
                    var client = new NodeClient(endPoint.EndPoint);
                    if (endPoint.NodeInfo == null)
                    {
                        endPoint.NodeInfo = (await client.DownloadNodeInfo(_configuration.NetworkPublicKey)).Data;
                    }

                    if (endPoint.NodeInfo != null && endPoint.NodeInfo.NodeId != _configuration.LocaleNodeInfo.NodeId)
                    {
                        var blockData = (await client.DownloadBlockData(chainSyncItem.ChainType, chainSyncItem.ChainId, chainSyncItem.ChainIndex, blockId)).Data;
                        if (blockData != null)
                        {
                            download.UpdateBlockData(blockData);
                            await HandleBlockData(blockData, nodeConnections);
                            goto end;
                        }
                        else
                        {
                            retry = true;
                        }
                    }
                }

                foreach (var connection in nodeConnections)
                {
                    await connection.Send(new NodeBlockDataRequestMessage(chainSyncItem.ChainType, blockId, chainSyncItem.ChainId, chainSyncItem.ChainIndex) { SignKey = _configuration.LocaleNodePrivateKey });
                    await Task.Delay(50);
                }

            end:
                lock (sync.Lock)
                {
                    download.Downloading = false;
                }
            }
            catch (Exception ex)
            {
                Log.HandleException(ex);
            }
            finally
            {
                sync.DownloadLock.Release();
            }

            if (retry && tries < 10)
            {
                await Task.Delay(50 * (tries + 1));
                TaskRunner.Run(() => DownloadBlock(blockId, chainSyncItem, nodeConnections, tries + 1));
            }
        }
    }
}
