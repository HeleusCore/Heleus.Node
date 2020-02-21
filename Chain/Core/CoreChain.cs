using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Chain.Blocks;
using Heleus.Chain.Storage;
using Heleus.Cryptography;
using Heleus.Operations;
using Heleus.ProofOfCouncil;

namespace Heleus.Chain.Core
{
    public sealed class CoreChain : Chain
    {
        public const int CoreChainId = Protocol.CoreChainId;

        readonly LazyLookupTable<long, CoreAccount> _accounts = new LazyLookupTable<long, CoreAccount> { LifeSpan = TimeSpan.FromMinutes(30), Depth = 4 };
        readonly LazyLookupTable<long, ChainInfo> _chains = new LazyLookupTable<long, ChainInfo> { LifeSpan = TimeSpan.FromMinutes(30), Depth = 4 };

        MetaDiscStorage _accountStorage => _metaDiscStorage[0];
        MetaDiscStorage _chainInfoStorage => _metaDiscStorage[1];

        public CoreChain(Node.Node node) : base(ChainType.Core, CoreChainId, 0, node)
        {
        }

        public override Task Initalize()
        {
            base.Initalize();

            var endPoints = new HashSet<AvailableEndPoint>();

            foreach (var ep in _node.NodeConfiguration.AutoConnectNodes)
            {
                if (IsValidAvailableEndPoint(ep))
                    endPoints.Add(new AvailableEndPoint(ep));
            }

            var chainInfo = GetChainInfo(ChainId);
            if (chainInfo != null)
            {
                var cep = chainInfo.GetPublicEndpoints();
                foreach (var ep in cep)
                {
                    var e = new Uri(ep);
                    if (IsValidAvailableEndPoint(e))
                        endPoints.Add(new AvailableEndPoint(e));
                }
            }

            foreach (var ep in _node.NodeConfiguration.BeaconNodes)
            {
                if (IsValidAvailableEndPoint(ep))
                    endPoints.Add(new AvailableEndPoint(ep));
            }

            AvailableEndpoints = new List<AvailableEndPoint>(endPoints);

            return Task.CompletedTask;
        }

        protected override bool NewMetaStorage()
        {
            try
            {
                _metaDiscStorage.Add(new MetaDiscStorage(_storage, ChainType, ChainId, ChainIndex, "coreaccounts", 58, DiscStorageFlags.FixedDataSize));
                _metaDiscStorage.Add(new MetaDiscStorage(_storage, ChainType, ChainId, ChainIndex, "chaininfo", 256, DiscStorageFlags.AppendOnly));

                GetCoreAccount(_accountStorage.EndIndex, false, false);
                GetChainInfo((int)_chainInfoStorage.EndIndex, false, false);

                return true;
            }
            catch(Exception ex)
            {
                Log.HandleException(ex, this);
            }

            return false;
        }

        protected override void BuildMetaData(TransactionDiscStorage transactionStorage, long sliceIndex)
        {
            var dirtyData = new DirtyData();
            Hash previousHash = null; // Todo, get hash from previous storage

            for (var transactionId = transactionStorage.StartIndex; transactionId <= transactionStorage.EndIndex; transactionId++)
            {
                if (transactionId <= LastProcessedTransactionId)
                    continue;

                var transactionData = transactionStorage.GetBlockDataRawIndex(transactionId);
                var item = new TransactionItem<CoreOperation>(transactionData);

                if (previousHash != null)
                {
                    if (!item.Validation.IsValid(previousHash, new ArraySegment<byte>(transactionData, 0, transactionData.Length - ValidationOperation.ValidationOperationDataSize)))
                    {
                        throw new Exception("Transaction Validation failed");
                    }
                }
                previousHash = item.Validation.Hash;

                ConsumeTransaction(dirtyData, item);
            }

            ProcessDirtyData(dirtyData);
        }

        void ProcessDirtyData(DirtyData dirtyData)
        {
            foreach (var accountId in dirtyData.DirtyAccounts)
            {
                var account = GetCoreAccount(accountId);
                _accountStorage.UpdateEntry(accountId, account.ToByteArray());
            }

            foreach (var chainId in dirtyData.DirtyChains)
            {
                var chain = GetChainInfo(chainId);
                _chainInfoStorage.UpdateEntry(chainId, chain.ToByteArray());
            }
        }

        protected override void ClearMetaData()
        {
            _accounts.Clear();
            _chains.Clear();
        }

        public override Task Start()
        {
            return base.Start();
        }

        internal CoreAccount GetCoreAccount(long accountId, bool add = true, bool clone = false)
        {
            lock (_lock)
            {
                if (_accounts.TryGetValue(accountId, out var account))
                    return (clone) ? new CoreAccount(account) : account;
            }

            var data = _accountStorage.GetBlockData(accountId);
            if (data == null)
                return null;

            using (var unpacker = new Unpacker(data))
            {
                var account = new CoreAccount(unpacker);
                if (add)
                {
                    lock (_lock)
                    {
                        if (_accounts.TryGetValue(accountId, out var acc))
                            return (clone) ? new CoreAccount(acc) : acc;

                        _accounts[accountId] = account;
                    }
                }

                if (account.AccountId != accountId)
                    throw new Exception("Invalid core account data");

                return (clone) ? new CoreAccount(account) : account;
            }
        }

        public CoreAccount GetCoreAccountData(long accountId)
        {
            return GetCoreAccount(accountId, false, true);
        }

        public bool CoreAccountExists(long accountId)
        {
            if (_accounts.Contains(accountId))
                return true;

            return _accountStorage.ContainsIndex(accountId);
        }

        internal bool TryGetChainInfo(int chainId, out ChainInfo chainInfo, bool add = true, bool clone = false)
        {
            chainInfo = GetChainInfo(chainId, add, clone);
            return chainInfo != null;
        }

        public PublicChainKey GetValidPublicChainKey(int chainId, uint chainIndex, short keyIndex, long timestamp)
        {
            TryGetChainInfo(chainId, out var chainInfo);
            return chainInfo?.GetValidChainKey(chainIndex, keyIndex, timestamp);
        }

        public PublicChainKey GetValidPublicChainKeyWithFlags(int chainId, uint chainIndex, short keyIndex, PublicChainKeyFlags keyFlags, long timestamp)
        {
            TryGetChainInfo(chainId, out var chainInfo);
            return chainInfo?.GetValidChainKeyWithFlags(chainIndex, keyIndex, timestamp, keyFlags);
        }

        public VoteMembers GetVoteMembers(ChainType chainType, int chainId, uint chainIndex, long timestamp)
        {
            var chainInfo = GetChainInfo(chainId);
            if (chainInfo != null)
            {
                var keyFlags = Block.GetRequiredChainVoteKeyFlags(chainType);
                var keys = chainInfo.GetValidChainKeysWithFlags(chainIndex, timestamp, keyFlags);
                return new VoteMembers(keys, chainId, chainType);
            }

            return null;
        }

        internal ChainInfo GetChainInfo(int chainId, bool add = true, bool clone = false)
        {
            lock (_lock)
            {
                if (_chains.TryGetValue(chainId, out var chain))
                    return (clone) ? new ChainInfo(chain) : chain;
            }

            var data = _chainInfoStorage.GetBlockData(chainId);
            if (data == null)
                return null;

            using (var unpacker = new Unpacker(data))
            {
                var chain = new ChainInfo(unpacker);
                if (add)
                {
                    lock (_lock)
                    {
                        if (_chains.TryGetValue(chainId, out var ch))
                            return (clone) ? new ChainInfo(ch) : ch;

                        _chains[chainId] = chain;
                    }
                }

                if (chain.ChainId != chainId)
                    throw new Exception("Invalid chain info");

                return (clone) ? new ChainInfo(chain) : chain;
            }
        }

        public ChainInfo GetChainInfoData(int chainId)
        {
            return GetChainInfo(chainId, false, true);
        }

        bool ConsumeTransaction(DirtyData dirtyData, TransactionItem<CoreOperation> coreItem)
        {
            var operation = coreItem.Transaction;
            var validation = coreItem.Validation;

            if (operation.OperationId <= LastProcessedTransactionId)
                return false;

            var type = operation.CoreOperationType;

            if (type == CoreOperationTypes.Account)
            {
                var ar = operation as AccountOperation;

                {
                    var ac = GetCoreAccount(ar.AccountId, false);
                    if (ac != null)
                        return false;
                }

                var account = new CoreAccount(ar.AccountId, ar.PublicKey)
                {
                    LastTransactionId = operation.OperationId
                };

                lock (_lock)
                    _accounts[account.AccountId] = account;
                _accountStorage.AddEntry(ar.AccountId, account.ToByteArray());

                return true;
            }

            if (type == CoreOperationTypes.ChainInfo)
            {
                var co = operation as ChainInfoOperation;

                {
                    var ci = GetChainInfo(co.ChainId, false);
                    if (co.IsNewChain && ci != null)
                        return false;
                    if (!co.IsNewChain && ci == null)
                        return false;
                }

                ChainInfo chain;
                if (co.IsNewChain)
                    chain = new ChainInfo(co.ChainId, co.AccountId, co.Name, co.Website);
                else
                {
                    chain = GetChainInfo(co.ChainId);
                    chain.UpdateInfo(co.Name, co.Website);
                }

                if (chain.ChainId != Protocol.CoreChainId)
                {
                    var account = GetCoreAccount(co.AccountId);
                    if (account == null)
                        return false;

                    account.LastTransactionId = operation.OperationId;
                    dirtyData.DirtyAccounts.Add(account.AccountId);
                }

                foreach (var endpoint in co.PublicEndpoints)
                    chain.AddPublicEndPoint(endpoint);

                foreach (var endpoint in co.RemovePublicEndPoints)
                    chain.RemovePublicEndPoint(endpoint);

                foreach (var chainKey in co.ChainKeys)
                    chain.AddChainKey(chainKey, co.Timestamp);

                foreach (var chainKeyIndex in co.RevokeChainKeys)
                    chain.RevokeChainKey(chainKeyIndex, operation.Timestamp);

                foreach (var purchase in co.Purchases)
                    chain.AddPurchase(purchase, co.Timestamp);

                foreach (var item in co.RemovePurchaseItems)
                    chain.RemovePurchaseItem(item, operation.Timestamp);

                lock (_lock)
                    _chains[chain.ChainId] = chain;

                if (co.IsNewChain)
                    _chainInfoStorage.AddEntry(co.ChainId, chain.ToByteArray());
                else
                    dirtyData.DirtyChains.Add(co.ChainId);

                return true;
            }

            if (type == CoreOperationTypes.Revenue)
            {
                var rev = operation as ChainRevenueInfoOperation;
                var chainId = rev.ChainId;
                var chain = GetChainInfo(chainId);
                if (chain == null)
                    return false;

                chain.AddRevenueInfo(rev.Revenue, rev.RevenueAccountFactor, rev.Timestamp);
                dirtyData.DirtyChains.Add(chainId);

                return true;
            }

            if (type == CoreOperationTypes.AccountUpdate)
            {
                var u = operation as AccountUpdateOperation;
                foreach (var item in u.Updates.Values)
                {
                    var account = GetCoreAccount(item.AccountId, true);
                    if (account == null)
                        return false;

                    account.UpdateBalance(item.Balance);
                    account.LastTransactionId = operation.OperationId;

                    dirtyData.DirtyAccounts.Add(item.AccountId);

                    foreach(var revenue in item.Revenues)
                    {
                        var chainId = revenue.ChainId;
                        var c = GetChainInfo(chainId);
                        c.AddTotalAccountPayout(revenue.Amount);

                        dirtyData.DirtyChains.Add(chainId);
                    }
                }

                return true;
            }

            if (type == CoreOperationTypes.BlockState)
            {
                var bi = operation as BlockStateOperation;
                foreach (var state in bi.BlockStates.Values)
                {
                    var chainId = state.ChainId;
                    var chainInfo = GetChainInfo(chainId);
                    if (chainInfo == null)
                        return false;

                    chainInfo.Update(state);
                    dirtyData.DirtyChains.Add(chainId);
                }

                return true;
            }

            return false;
        }

        public bool IsBlockSignatureValid(BlockData blockData)
        {
            var block = blockData.Block;
            if (block != null)
            {
                var chainType = blockData.ChainType;
                if (chainType == ChainType.Core)
                {
                    if (block.BlockId == Protocol.GenesisBlockId)
                    {
                        return blockData.Signatures.IsSignatureValid(_node.NodeConfiguration.NetworkPublicKey, Protocol.GenesisBlockNetworkKeyIssuer, block);
                    }
                }

                var voteKeys = GetVoteMembers(block.ChainType, block.ChainId, block.ChainIndex, block.Timestamp);
                if (voteKeys != null)
                {
                    return voteKeys.IsBlockSignatureValid(blockData);
                }
            }

            return false;
        }

        public bool IsBlockProposalSignatureValid(Block block, BlockProposalSignatures proposalSignatures)
        {
            if (block != null && proposalSignatures != null)
            {
                var voteKeys = GetVoteMembers(block.ChainType, block.ChainId, block.ChainIndex, block.Timestamp);
                if (voteKeys != null)
                {
                    return voteKeys.IsBlockSignatureValid(block, proposalSignatures);
                }
            }

            return false;
        }

        public void ConsumeCoreBlockData(BlockData<CoreBlock> blockData)
        {
            var block = blockData.Block;
            if (block.BlockId < LastProcessedBlockId)
                return;

            var dirtyData = new DirtyData();

            var i = 0;
            foreach (var item in block.Items)
            {
                var operation = item.Transaction;
                if (ConsumeTransaction(dirtyData, item))
                {
                    TransactionStorage.Add(block.BlockId, item);
                    i++;
                }
            }

            ProcessDirtyData(dirtyData);

            var lastTransactionid = LastProcessedTransactionId;
            var count = block.Items.Count;
            if (count > 0)
            {
                lastTransactionid = block.Items[count - 1].Transaction.OperationId;
            }

            foreach (var metaStorage in _metaDiscStorage)
            {
                metaStorage.LastBlockId = block.BlockId;
                metaStorage.LastTransactionId = lastTransactionid;
                metaStorage.Commit();
            }

            TransactionStorage.Save();
        }
    }
}
