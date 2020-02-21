using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Chain.Storage;
using Heleus.Cryptography;
using Heleus.Network.Client;
using Heleus.Operations;
using Heleus.Transactions;

namespace Heleus.Chain.Blocks
{
    public class BlockTransactionHistory
    {
        readonly List<BlockData> _blocks = new List<BlockData>();
        readonly Dictionary<long, long> _identifiers = new Dictionary<long, long>();
        readonly LazyLookupTable<long, KeyCheck> _registrationHistory = new LazyLookupTable<long, KeyCheck> { LifeSpan = TimeSpan.FromHours(5) };

        readonly BlockStorage _blockStorage;
        readonly int _maxBlockCount;
        readonly SemaphoreSlim _sem = new SemaphoreSlim(1);
        readonly object _lock = new object();

        public BlockTransactionHistory(BlockStorage blockStorage, int maxBlockCount)
        {
            _blockStorage = blockStorage;
            _maxBlockCount = maxBlockCount;
        }

        public async Task<bool> Init(BlockData blockData)
        {
            Clear();

            if (blockData == null)
                return false;

            var latest = blockData.Block;
            var start = latest.BlockId - _maxBlockCount;

            for (var i = start; i <= latest.BlockId; i++)
            {
                blockData = await _blockStorage.GetBlockData(i);
                if (blockData != null)
                {
                    if (!AddBlock(blockData))
                        return false;
                }
            }
            return true;
        }

        public async Task<bool> Update(BlockData latest)
        {
            await _sem.WaitAsync();

            var last = _blocks.LastOrDefault();

            if (last != null)
            {
                if (last.Block.BlockId != latest.Block.BlockId)
                {
                    for (var i = (last.Block.BlockId + 1); i <= latest.Block.BlockId; i++)
                    {
                        var blockData = await _blockStorage.GetBlockData(i);
                        if (blockData != null)
                        {
                            if (!AddBlock(blockData))
                            {
                                _sem.Release();
                                return false;
                            }
                        }
                    }
                }
            }
            else
            {
                AddBlock(latest);
            }

            _sem.Release();
            return true;
        }

        bool AddBlock(BlockData blockData)
        {
            lock (_lock)
            {
                var last = _blocks.LastOrDefault();
                if (last != null)
                {
                    if (last.Block.BlockId + 1 != blockData.Block.BlockId)
                    {
                        Log.Error("Whoopsie");
                        return false;
                    }
                }
                _blocks.Add(blockData);

                if (blockData.ChainType == ChainType.Core)
                {
                    var coreBlock = blockData.Block as CoreBlock;

                    foreach (var op in coreBlock.Items)
                    {
                        if (op.Transaction.CoreOperationType == CoreOperationTypes.Account)
                        {
                            var reg = op.Transaction as AccountOperation;
                            var key = reg.PublicKey;
                            var uid = BitConverter.ToInt64(key.RawData.Array, key.RawData.Offset);

                            _registrationHistory.Add(uid, new KeyCheck(reg.AccountId, Protocol.CoreChainId, Protocol.CoreAccountSignKeyIndex, uid));
                        }
                    }

                    foreach (var transaction in coreBlock.Transactions)
                    {
                        _identifiers[transaction.UniqueIdentifier] = transaction.TransactionId;
                    }
                }
                else if (blockData.ChainType == ChainType.Data)
                {
                    var chainBlock = blockData.Block as DataBlock;
                    foreach (var transaction in chainBlock.Transactions)
                    {
                        _identifiers[transaction.UniqueIdentifier] = transaction.TransactionId;
                    }
                }
                else if (blockData.ChainType == ChainType.Service)
                {
                    var serviceBlock = blockData.Block as ServiceBlock;
                    foreach (var transaction in serviceBlock.Transactions)
                    {
                        _identifiers[transaction.UniqueIdentifier] = transaction.TransactionId;

                        if (transaction.TransactionType == ServiceTransactionTypes.Join)
                        {
                            var join = transaction as JoinServiceTransaction;
                            if (join.AccountKey != null)
                            {
                                var key = join.AccountKey.PublicKey;
                                var uid = BitConverter.ToInt64(key.RawData.Array, key.RawData.Offset);
                                _registrationHistory.Add(uid, new KeyCheck(join.AccountId, join.TargetChainId, join.AccountKey.KeyIndex, uid));
                            }
                        }
                    }
                }
                else if (blockData.ChainType == ChainType.Maintain)
                {

                }
                else
                {
                    throw new Exception($"Invalid ChainType {blockData.ChainType}.");
                }

                while (_blocks.Count > _maxBlockCount)
                {
                    var first = _blocks[0];

                    if (first.ChainType == ChainType.Core)
                    {
                        var coreBlock = first.Block as CoreBlock;
                        foreach (var transaction in coreBlock.Transactions)
                        {
                            _identifiers.Remove(transaction.UniqueIdentifier);
                        }
                    }
                    else if (first.ChainType == ChainType.Service)
                    {
                        var serviceBlock = first.Block as ServiceBlock;
                        foreach (var transaction in serviceBlock.Transactions)
                        {
                            _identifiers.Remove(transaction.UniqueIdentifier);
                        }
                    }
                    else if (first.ChainType == ChainType.Data)
                    {
                        var chainBlock = first.Block as DataBlock;
                        foreach (var transaction in chainBlock.Transactions)
                        {
                            _identifiers.Remove(transaction.UniqueIdentifier);
                        }
                    }
                    else if (first.ChainType == ChainType.Maintain)
                    {

                    }

                    _blocks.RemoveAt(0);
                }

                return true;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _blocks.Clear();
                _identifiers.Clear();
            }
        }

        public bool ContainsTransactionIdentifier(Transaction transaction)
        {
            lock (_lock)
                return _identifiers.ContainsKey(transaction.UniqueIdentifier);
        }

        public long GetTransactionIdFromIdentiifer(Transaction transaction)
        {
            lock (_lock)
            {
                if (_identifiers.TryGetValue(transaction.UniqueIdentifier, out var id))
                    return id;
                return 0;
            }
        }

        public KeyCheck GetRegistrationJoinHistory(Transaction transaction)
        {
            Key key = null;

            if (transaction.OperationType == (ushort)CoreTransactionTypes.AccountRegistration)
            {
                key = (transaction as AccountRegistrationCoreTransaction).PublicKey;
            }
            else if (transaction.OperationType == (ushort)ServiceTransactionTypes.Join)
            {
                var signedKey = (transaction as JoinServiceTransaction).AccountKey;
                key = signedKey?.PublicKey;
            }

            if (key != null)
            {
                var uid = BitConverter.ToInt64(key.RawData.Array, key.RawData.Offset);
                _registrationHistory.TryGetValue(uid, out var history);
                return history;
            }

            return null;
        }

        public KeyCheck GetRegistrationJoinHistory(long identifier)
        {
            _registrationHistory.TryGetValue(identifier, out var history);
            return history;
        }
    }
}
