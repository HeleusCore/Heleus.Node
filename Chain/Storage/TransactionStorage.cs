using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Heleus.Base;
using Heleus.Chain.Blocks;
using Heleus.Operations;

namespace Heleus.Chain.Storage
{
    public class TransactionStorage : IDisposable
    {
        public readonly int ChainId;
        public readonly uint ChainIndex;

        public readonly string TransactionsPath;
        public readonly string FullTransactionsStoragePath;

        public int CurrentSliceId
        {
            get
            {
                lock (_lock)
                    return _currentSliceId;
            }
        }

        readonly LazyLookupTable<int, DiscStorage> _readonlyTransactionSlices = new LazyLookupTable<int, DiscStorage> { OnRemove = LazyLookupTable<int, DiscStorage>.DisposeItems, LifeSpan = TimeSpan.FromMinutes(2) };

        readonly SortedList<long, TransactionSliceInfo> _slices = new SortedList<long, TransactionSliceInfo>();
        readonly DiscStorageFlags _storageFlags;
        readonly int _blockSize;

        readonly object _lock = new object();

        int _currentSliceId;
        long _currentFirsBlockId;
        long _currentBlockId;

        TransactionDiscStorage _transactionSlice;
        readonly Base.Storage _storage;

        public static string GetTransactionStoragePath(ChainType chainType, int chainId, uint chainIndex)
        {
            return $"chains/{chainType.ToString().ToLower()}/{chainId}_{chainIndex}/transactions/";
        }

        public static void CreateRequiredDirectories(Base.Storage storage, ChainType chainType, int chainId, uint chainIndex)
        {
            var path = GetTransactionStoragePath(chainType, chainId, chainIndex);

            if (!storage.CreateDirectory(path))
                throw new Exception($"Could not create directory {path}.");
        }

        public SliceInfo GetStoredSliceInfo()
        {
            var first = -1L;
            var last = -1L;

            lock (_lock)
            {
                var c = _slices.Count;
                if (c > 0)
                {
                    first = _slices[0].SliceId;
                    last = _slices[c - 1].SliceId;
                }
            }

            return new SliceInfo(first, last);
        }

        public bool IsTransactionSliceAvailable(long sliceIndex)
        {
            lock (_lock)
            {
                _slices.TryGetValue(sliceIndex, out var info);
                if (info != null)
                    return info.Finalized;
            }

            return false;
        }

        public TransactionStorage(Base.Storage storage, ChainType chainType, int chainId, uint chainIndex)
        {
            _blockSize = 0;
            _storageFlags = DiscStorageFlags.DynamicBlockSize;
            _storage = storage;

            ChainId = chainId;
            ChainIndex = chainIndex;

            TransactionsPath = GetTransactionStoragePath(chainType, chainId, chainIndex);
            FullTransactionsStoragePath = Path.Combine(storage.Root.FullName, TransactionsPath);

            CreateRequiredDirectories(storage, chainType, chainId, chainIndex);

            _currentSliceId = 0;
            _slices = GetTransactionSlices(storage, chainType, chainId, chainIndex, false);

            // remove the last "hot" slice
            if (_slices.Count > 0)
            {
                var last = _slices.Last().Value;
                if (last.Finalized)
                {
                    _currentSliceId = last.SliceId + 1;
                }
                else
                {
                    _currentSliceId = last.SliceId;
                    _slices.RemoveAt(_slices.Count - 1);
                }
            }

            _transactionSlice = new TransactionDiscStorage(_storage, Path.Combine(TransactionsPath, _currentSliceId.ToString()), _blockSize, _storageFlags);

            _currentFirsBlockId = _transactionSlice.FirstBlockId;
            _currentBlockId = _transactionSlice.LastBlockId;
        }

        ~TransactionStorage()
        {
            Dispose();
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_transactionSlice != null)
                {
                    _transactionSlice.Dispose();
                    _transactionSlice = null;
                }
            }

            GC.SuppressFinalize(this);
        }

        public TransactionItem<TransactionType> GetTransactionItem<TransactionType>(long transactionId) where TransactionType : Operation
        {
            var data = GetTransactionItemData(transactionId);
            if (data != null)
                return new TransactionItem<TransactionType>(data);
            return null;
        }

        public byte[] GetTransactionItemData(long transactionId)
        {
            var sliceIndex = TransactionSliceInfo.GetSliceIndex(transactionId);
            var inCurrentSlice = sliceIndex == _currentSliceId;

            if (sliceIndex >= 0)
            {
                DiscStorage storage = null;

                if (!inCurrentSlice)
                {
                    TransactionSliceInfo slice = null;
                    lock (_lock)
                        slice = _slices[(int)sliceIndex];

                    lock (_lock)
                    {
                        _readonlyTransactionSlices.TryGetValue(slice.SliceId, out storage);
                    }

                    if (storage == null)
                    {
                        storage = GetTransactionStorage(slice.SliceId);

                        lock (_lock)
                        {
                            _readonlyTransactionSlices[slice.SliceId] = storage;
                        }
                    }
                }
                else
                {
                    lock (_lock)
                    {
                        storage = _transactionSlice;
                    }
                }

                if (storage != null)
                {
                    if (transactionId >= 0)
                        return storage.GetBlockData(transactionId);
                }
            }

            return null;
        }

        public TransactionDiscStorage GetTransactionStorage(long sliceId)
        {
            return new TransactionDiscStorage(_storage, Path.Combine(TransactionsPath, sliceId.ToString()), _blockSize, _storageFlags | DiscStorageFlags.Readonly);
        }

        public long GetSliceIdForTransactionId(long transactionId)
        {
            var idx = TransactionSliceInfo.GetSliceIndex(transactionId);
            if (idx < 0)
                return 0;

            lock (_lock)
                return _slices.Count > 0 && _slices.Count > idx ? idx : 0;
        }

        public static SortedList<long, TransactionSliceInfo> GetTransactionSlices(Base.Storage storage, ChainType chainType, int chainId, uint chainIndex, bool finalizedOnly)
        {
            var chainPath = GetTransactionStoragePath(chainType, chainId, chainIndex);
            var discSlices = storage.GetFiles(chainPath, "*.header");

            var result = new SortedList<long, TransactionSliceInfo>();
            foreach (var slice in discSlices)
            {
                var sliceId = int.Parse(slice.Name.Split('.')[0]);
                var info = DiscStorage.GetHeader(storage, Path.Combine(chainPath, sliceId.ToString()));
                var userData = new Unpacker(info.UserData);

                var firstBlockId = Protocol.InvalidBlockId;
                var blockId = Protocol.InvalidBlockId;
                var split = false;

                if (userData.UnpackBool())
                {
                    firstBlockId = userData.UnpackLong();
                    blockId = userData.UnpackLong();
                    split = userData.UnpackBool();
                }

                if (!finalizedOnly || split)
                    result.Add(sliceId, new TransactionSliceInfo(sliceId, firstBlockId, blockId, split, info.Count, info.StartIndex, info.EndIndex));
            }

            return result;
        }

        internal void Save()
        {
            _transactionSlice.Commit();
        }

        internal void Add<TransactionType>(long blockId, TransactionItem<TransactionType> item) where TransactionType : Operation
        {
            var transaction = item.Transaction;
            var transactionId = transaction.OperationId;

            var split = TransactionSliceInfo.IsSliceSplit(transactionId);

            lock (_lock)
            {
                _transactionSlice.AddEntry(transactionId, item.ToByteArray());

                _currentBlockId = blockId;
                if (_currentFirsBlockId == Protocol.InvalidBlockId)
                    _currentFirsBlockId = blockId;

                _transactionSlice.FirstBlockId = _currentFirsBlockId;
                _transactionSlice.LastBlockId = _currentBlockId;
                _transactionSlice.Split = split;
            }

            if (split)
            {
                Save();
                Split(blockId);
            }
        }

        void Split(long blockId)
        {
            lock (_lock)
            {
                if (_transactionSlice.Length <= 0)
                    return;

                if (_currentFirsBlockId == Protocol.InvalidBlockId)
                    _currentFirsBlockId = blockId;
                _currentBlockId = blockId;

                _slices.Add(_currentSliceId, new TransactionSliceInfo(_currentSliceId, _currentFirsBlockId, _currentBlockId, true, _transactionSlice.Length, _transactionSlice.StartIndex, _transactionSlice.EndIndex));
            }

            _transactionSlice.Close();

            DiscStorage.BuildChecksum(_storage, Path.Combine(TransactionsPath, _currentSliceId.ToString()));

            lock (_lock)
            {
                _currentSliceId++;
                _currentBlockId = Protocol.InvalidBlockId;
                _currentFirsBlockId = Protocol.InvalidBlockId;

                _transactionSlice = new TransactionDiscStorage(_storage, Path.Combine(TransactionsPath, _currentSliceId.ToString()), _blockSize, _storageFlags)
                {
                    FirstBlockId = _currentFirsBlockId,
                    LastBlockId = _currentBlockId,
                    Split = false
                };
            }

            Save();
        }
    }
}
