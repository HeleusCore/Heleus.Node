using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Chain.Blocks;
using Heleus.Transactions;

namespace Heleus.Chain.Storage
{
    public class BlockStorage : ILogger
    {
        public string LogName => GetType().Name;

        public readonly ChainType ChainType;
        public readonly int ChainId;
        public readonly uint ChainIndex;

        public Block LastBlock { get; private set; }
        public BlockData LastBlockData { get; private set; }

        public readonly BlockTransactionHistory History;

        public readonly string FullBlockStoragePath;

        public long LastStoredBlockId
        {
            get
            {
                lock (_lock)
                {
                    if (_blockSlices.Count == 0)
                        return Protocol.InvalidBlockId;
                    return _blockSlices.Last().Value.LastBlockId;
                }
            }
        }

        public bool IsBlockSliceAvailable(long sliceIndex)
        {
            lock (_lock)
            {
                _blockSlices.TryGetValue(sliceIndex, out var info);
                if (info != null)
                    return info.Finalized;
            }

            return false;
        }

        public SliceInfo GetStoredSliceInfo()
        {
            var first = -1L;
            var last = -1L;

            lock (_lock)
            {
                var c = _blockSlices.Count;
                if (c > 0)
                {
                    first = _blockSlices[0].SliceIndex;
                    last = _blockSlices[c - 1].SliceIndex;
                }
            }

            return new SliceInfo(first, last);
        }

        public LastBlockInfo LastBlockInfo
        {
            get
            {
                lock(_lock)
                {
                    var lastBlockId = Protocol.InvalidBlockId;
                    var lastTransactionId = Operations.Operation.InvalidTransactionId;

                    if(LastBlock != null)
                    {
                        lastBlockId = LastBlock.BlockId;
                        lastTransactionId = LastBlock.LastTransactionId;
                    }

                    return new LastBlockInfo(ChainType, ChainId, ChainIndex, lastBlockId, lastTransactionId);
                }
            }
        }

        readonly object _lock = new object();
        readonly SemaphoreSlim _consuming = new SemaphoreSlim(1);
        volatile bool _active;

        readonly LazyLookupTable<long, BlockData> _blockData = new LazyLookupTable<long, BlockData> { Depth = 3, LifeSpan = TimeSpan.FromSeconds(10) };
        readonly ConcurrentLoader<long, BlockData> _blockLoader;
        readonly LazyLookupTable<long, BlockDiscStorage> _blocksStorage = new LazyLookupTable<long, BlockDiscStorage> { LifeSpan = TimeSpan.FromMinutes(10), OnRemove = LazyLookupTable<long, BlockDiscStorage>.DisposeItems, };
        BlockDiscStorage _blockDiscStorage;

        SortedList<long, BlockSliceInfo> _blockSlices = new SortedList<long, BlockSliceInfo>();

        readonly Base.Storage _storage;

        public static string GetBlockStoragePath(ChainType chainType, int chainId, uint chainIndex)
        {
            return $"chains/{chainType.ToString().ToLower()}/{chainId}_{chainIndex}/blocks/";
        }

        public static void CreateRequiredDirectories(Base.Storage storage, ChainType chainType, int chainId, uint chainIndex)
        {
            var path = GetBlockStoragePath(chainType, chainId, chainIndex);

            if (!storage.CreateDirectory(path))
                throw new Exception($"Could not create directory {path}.");
        }

        readonly string _blocksPath;

        public BlockStorage(ChainType chainType, int chainId, uint chainIndex, Node.Node node)
        {
            ChainType = chainType;
            ChainId = chainId;
            ChainIndex = chainIndex;

            _storage = node.Storage;

            _blocksPath = GetBlockStoragePath(chainType, chainId, chainIndex);
            FullBlockStoragePath = Path.Combine(_storage.Root.FullName, _blocksPath);

            CreateRequiredDirectories(_storage, chainType, chainId, chainIndex);

            CheckBlockSlices();

            _blockLoader = new ConcurrentLoader<long, BlockData>(QueryBlock, LoadBlock);

            History = new BlockTransactionHistory(this, 25);
        }

        public static SortedList<long, BlockSliceInfo> GetBlockSlices(Base.Storage storage, ChainType chainType, int chainId, uint chainIndex, bool finalizedOnly)
        {
            var result = new SortedList<long, BlockSliceInfo>();
            var path = GetBlockStoragePath(chainType, chainId, chainIndex);
            var blockStorages = storage.GetFiles(path, "*.header");
            foreach (var blockStorage in blockStorages)
            {
                var sliceIndex = long.Parse(blockStorage.Name.Split('.')[0]);
                var blockInfo = new BlockSliceInfo(sliceIndex);

                var header = DiscStorage.GetHeader(storage, Path.Combine(path, sliceIndex.ToString()));
                if (header.Count > 0)
                {
                    blockInfo.FirstBlockId = header.StartIndex;
                    blockInfo.LastBlockId = header.EndIndex;
                }

                if (finalizedOnly)
                {
                    if (blockInfo.Finalized)
                        result.Add(sliceIndex, blockInfo);
                }
                else
                {
                    result.Add(sliceIndex, blockInfo);
                }
            }

            return result;
        }

        public async Task Start()
        {
            Stop();

            lock (_lock)
            {
                RefreshBlockSlices();
            }

            await History.Init(LastBlockData);

            lock (_lock)
                _active = true;
        }

        public void Stop()
        {
            History.Clear();

            lock (_lock)
                _active = false;
        }

        void RefreshBlockSlices()
        {
            lock (_lock)
            {
                _blockSlices = GetBlockSlices(_storage, ChainType, ChainId, ChainIndex, false);

                var last = _blockSlices.LastOrDefault();
                if (last.Value != null)
                {
                    LastBlockData = GetBlockData(last.Value.LastBlockId).Result;
                    LastBlock = LastBlockData.Block;
                }
            }
        }

        void CheckBlockSlices()
        {
            var slices = GetBlockSlices(_storage, ChainType, ChainId, ChainIndex, false);

            BlockSliceInfo previous = null;
            foreach (var info in slices)
            {
                var slice = info.Value;

                if (!slice.Valid)
                {
                    Log.Warn($"Removing all block slices, invalid slice found {slice.SliceIndex}.", this);
                    RemoveAllBlockSlices();
                    return;
                }

                if (previous == null)
                {
                    previous = slice;
                }
                else
                {
                    if (!previous.Finalized || (previous.LastBlockId + 1) != slice.FirstBlockId)
                    {
                        Log.Warn($"Removing all block slices, invalid slice order found {slice.SliceIndex}.", this);

                        RemoveAllBlockSlices();
                        return;
                    }
                    previous = info.Value;
                }

                // missing checksums, happend before, possibly through power outage
                if (slice.Finalized)
                {
                    var result = DiscStorage.CheckDiscStorage(_storage, Path.Combine(_blocksPath, slice.SliceIndex.ToString()));
                    if (result == DiscStorage.CheckDiscStorageResult.CheckumFailed)
                    {
                        Log.Warn($"Removing all block slices, invalid slice checksum found {slice.SliceIndex}.", this);
                        RemoveAllBlockSlices();
                        return;
                    }
                    if (result == DiscStorage.CheckDiscStorageResult.DataCrcError)
                    {
                        Log.Warn($"Removing all block slices, slice data crc error found {slice.SliceIndex}.", this);

                        RemoveAllBlockSlices();
                        return;
                    }
                    if (result == DiscStorage.CheckDiscStorageResult.MissingChecksum)
                    {
                        Log.Info($"Rebuilding checksum for block slice {slice.SliceIndex}.", this);
                        DiscStorage.BuildChecksum(_storage, Path.Combine(_blocksPath, slice.SliceIndex.ToString()));
                    }
                }
            }
        }

        void RemoveAllBlockSlices()
        {
            lock (_lock)
            {
                var blocks = _storage.GetFiles(_blocksPath, "*.header");
                foreach (var block in blocks)
                {
                    try
                    {
                        var id = long.Parse(block.Name.Split('.')[0]);
                        RemoveBlockSlice(id);
                    }
                    catch (Exception ex)
                    {
                        Log.IgnoreException(ex, this);
                    }
                }

                _blockSlices.Clear();

                LastBlockData = null;
                LastBlock = null;
            }
        }

        void RemoveBlockSlice(long sliceIndex)
        {
            try
            {
                _storage.DeleteFile(Path.Combine(_blocksPath, sliceIndex + ".data"));
                _storage.DeleteFile(Path.Combine(_blocksPath, sliceIndex + ".header"));
                _storage.DeleteFile(Path.Combine(_blocksPath, sliceIndex + ".checksums"));
            }
            catch (Exception ex)
            {
                Log.IgnoreException(ex, this);
            }
        }

        Task<BlockData> QueryBlock(long blockid)
        {
            return Task.FromResult(_blockData.Get(blockid));
        }

        Task<BlockData> LoadBlock(long blockid)
        {
            BlockData result = null;
            try
            {
                if (blockid >= Protocol.GenesisBlockId)
                {
                    var sliceIndex = BlockSliceInfo.GetSliceIndex(blockid);
                    var blockStorage = _blocksStorage.Get(sliceIndex);
                    if (blockStorage == null)
                    {
                        blockStorage = new BlockDiscStorage(_storage, ChainType, ChainId, ChainIndex, sliceIndex, true);
                        if (blockStorage.Length > 0)
                            _blocksStorage.Add(sliceIndex, blockStorage);
                    }

                    if (blockStorage.Length > 0)
                    {
                        var blockData = blockStorage.GetBlockData(blockid);
                        if (blockData != null)
                        {
                            result = BlockData.Restore(blockData);
                            _blockData.Add(blockid, result);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.HandleException(ex, this);
            }

            return Task.FromResult(result);
        }

        public Task<BlockData> GetBlockData(long blockId)
        {
            return _blockLoader.Get(blockId);
        }

        public TransactionResultTypes HistoryContainsTransactionOrRegistration(Transaction transaction)
        {
            if (transaction.OperationType == (ushort)CoreTransactionTypes.AccountRegistration || transaction.OperationType == (ushort)ServiceTransactionTypes.Join)
            {
                var alreadyRegisterd = History.GetRegistrationJoinHistory(transaction) != null;
                if (alreadyRegisterd)
                    return TransactionResultTypes.AlreadyJoined;
                return TransactionResultTypes.Ok;
            }

            if (History.ContainsTransactionIdentifier(transaction))
                return TransactionResultTypes.AlreadyProcessed;

            return TransactionResultTypes.Ok;
        }

        public long GetTransactionIdFromUniqueIdentifier(Transaction transaction)
        {
            return History.GetTransactionIdFromIdentiifer(transaction);
        }

        public async Task<BlockConsumeResult> StoreBlock(BlockData blockData)
        {
            var block = blockData.Block;

            lock (_lock)
            {
                if (!_active)
                    return BlockConsumeResult.NotActive;
            }

            await _consuming.WaitAsync();

            var next = LastStoredBlockId + 1;

            if (block.BlockId > next)
            {
                _consuming.Release();
                return BlockConsumeResult.SyncRequired;
            }

            if (block.BlockId < next)
            {
                lock (_lock)
                {
                    if (_blockSlices.Count > 0)
                    {
                        if (block.BlockId != (next - 1))
                        {
                            _consuming.Release();
                            return BlockConsumeResult.MissingBlock;
                        }
                    }
                }
            }

            if (block.BlockId < next)
            {
                _consuming.Release();
                return BlockConsumeResult.Ok;
            }

            if (LastBlock != null)
            {
                if (block.PreviousBlockHash != LastBlock.BlockHash)
                {
                    _consuming.Release();
                    return BlockConsumeResult.InvalidHash;
                }
            }

            var sliceIndex = BlockSliceInfo.GetSliceIndex(block.BlockId);
            if (_blockDiscStorage == null)
                _blockDiscStorage = new BlockDiscStorage(_storage, ChainType, ChainId, ChainIndex, sliceIndex, false);

            if (sliceIndex != _blockDiscStorage.SliceIndex)
            {
                _blockDiscStorage.Dispose();
                DiscStorage.BuildChecksum(_storage, Path.Combine(_blocksPath, _blockDiscStorage.SliceIndex.ToString()));
                _blockDiscStorage = new BlockDiscStorage(_storage, ChainType, ChainId, ChainIndex, sliceIndex, false);
            }

            _blockDiscStorage.AddEntry(block.BlockId, blockData.ToByteArray());
            _blockDiscStorage.Commit();
            _blockData.Add(block.BlockId, blockData);

            lock (_lock)
            {
                if (_blockSlices.TryGetValue(sliceIndex, out var blockStorageInfo))
                {
                    blockStorageInfo.LastBlockId = block.BlockId;
                }
                else
                {
                    blockStorageInfo = new BlockSliceInfo(sliceIndex)
                    {
                        FirstBlockId = block.BlockId,
                        LastBlockId = block.BlockId
                    };

                    _blockSlices.Add(sliceIndex, blockStorageInfo);
                }
            }

            if (!await History.Update(blockData))
                await History.Init(blockData);

            lock (_lock)
            {
                LastBlock = block;
                LastBlockData = blockData;
            }

            _consuming.Release();

            return BlockConsumeResult.Ok;
        }
    }
}
