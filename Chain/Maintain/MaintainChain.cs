using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Chain.Blocks;
using Heleus.Chain.Core;
using Heleus.Chain.Service;
using Heleus.Chain.Storage;
using Heleus.Cryptography;
using Heleus.Messages;
using Heleus.Operations;
using Heleus.Transactions;
using Heleus.Transactions.Features;

namespace Heleus.Chain.Maintain
{
    public sealed class MaintainChain : FeatureChain, IMaintainChain
    {
        public readonly ServiceChain ServiceChain;

        readonly object _revenueLock = new object();

        readonly LazyLookupTable<long, MaintainAccount> _maintainAccounts = new LazyLookupTable<long, MaintainAccount> { LifeSpan = TimeSpan.FromMinutes(30), Depth = 4 };
        readonly LazyLookupTable<int, RevenueReceivers> _revenueReceivers = new LazyLookupTable<int, RevenueReceivers> { LifeSpan = TimeSpan.FromDays(1), Depth = 3 };

        MetaDiscStorage _maintainAccountStorage => _metaDiscStorage[0];
        RevenueMetaDiscStorage _revenueReceiversStroage => (RevenueMetaDiscStorage)_metaDiscStorage[1];


        MetaDiscStorage _storedRevenueProposalsStorage;
        readonly LazyLookupTable<int, HashSet<long>> _storedRevenueProposals = new LazyLookupTable<int, HashSet<long>> { LifeSpan = TimeSpan.FromDays(1), Depth = 3 };
        readonly LazyLookupTable<int, HashSet<long>> _pendingRevenueProposals = new LazyLookupTable<int, HashSet<long>> { LifeSpan = TimeSpan.FromDays(1), Depth = 3 };

        public MaintainChain(ServiceChain serviceChain, Node.Node node) : base(ChainType.Maintain, serviceChain.ChainId, 0, node)
        {
            ServiceChain = serviceChain;
        }

        public override bool FeatureAccountExists(long accountId)
        {
            return ServiceChain.ServiceAccountExists(accountId);
        }

        public override FeatureAccount GetFeatureAccount(long accountId)
        {
            return GetMaintainAccount(accountId, true);
        }

        public override async Task Initalize()
        {
            await base.Initalize();
            TaskRunner.Run(() => RevenueProposalLoop());
        }

        protected override bool NewMetaStorage()
        {
            try
            {
                _metaDiscStorage.Add(new MetaDiscStorage(_storage, ChainType, ChainId, ChainIndex, "maintainaccounts", 256, DiscStorageFlags.UnsortedDynamicIndex | DiscStorageFlags.AppendOnly));
                _metaDiscStorage.Add(new RevenueMetaDiscStorage(_storage, ChainType, ChainId, ChainIndex, "revenue"));

                // do not add it to the meta storage, there is no need for a rebuild if it's missing/corrupt
                _storedRevenueProposalsStorage = new MetaDiscStorage(_storage, ChainType, ChainId, ChainIndex, "revenueproposals", 1024, DiscStorageFlags.UnsortedDynamicIndex | DiscStorageFlags.AppendOnly);

                return base.NewMetaStorage();
            }
            catch (Exception ex)
            {
                Log.HandleException(ex, this);
            }

            return false;
        }

        protected override void BuildMetaData(TransactionDiscStorage transactionStorage, long sliceIndex)
        {
            var dirtyData = new DirtyData();
            var commitItems = NewCommitItems();
            Hash previousHash = null; // Todo, get hash from previous storage

            for (var transactionId = transactionStorage.StartIndex; transactionId <= transactionStorage.EndIndex; transactionId++)
            {
                if (transactionId <= LastProcessedTransactionId)
                    continue;

                var transactionData = transactionStorage.GetBlockDataRawIndex(transactionId);
                var item = new TransactionItem<MaintainTransaction>(transactionData);

                if (previousHash != null)
                {
                    if (!item.Validation.IsValid(previousHash, new ArraySegment<byte>(transactionData, 0, transactionData.Length - ValidationOperation.ValidationOperationDataSize)))
                    {
                        throw new Exception("Transaction Validation failed");
                    }
                }
                previousHash = item.Validation.Hash;

                ConsumeTransaction(dirtyData, commitItems, item);
            }

            CommmitDirtyData(dirtyData, commitItems);
            commitItems.Commit();
        }

        class Lehmer
        {
            int _seed;

            public Lehmer(int seed)
            {
                _seed = seed;

                for(var i = 0; i < 257; i++)
                    Next();
            }

            public int Next()
            {
                return _seed = (int)((48271L * _seed) % int.MaxValue);
            }

            public int Next(int max)
            {
                return Next() % max;
            }
        }

        void CommmitDirtyData(DirtyData dirtyData, CommitItems commitItems)
        {
            lock (_revenueLock)
            {
                foreach(var tick in dirtyData.DirtyRevenueTicks)
                {
                    var revenues = GetRevenueReceivers(tick);

                    _revenueReceiversStroage.UpdateEntry(tick, revenues.ToByteArray());
                    _revenueReceiversStroage.LastAvailableTick = Math.Max(tick, _revenueReceiversStroage.LastAvailableTick);
                }

                var last = GetRevenueReceivers(_revenueReceiversStroage.LastAvailableTick);
                if(last != null)
                {
                    var previous = GetRevenueReceivers(last.PreviousTick);
                    var lastProcessed = _revenueReceiversStroage.LastProcessedTick;

                    while (previous != null)
                    {
                        if (previous.Tick <= lastProcessed)
                            break;

                        var tick = previous.Tick;
                        var accounts = previous.Accounts;
                        var count = accounts.Count;
                        var revenueInfo = previous.RevenueInfo;

                        var tickRevenue = revenueInfo.Revenue * revenueInfo.AccountRevenueFactor;
                        var accountRevenue = tickRevenue / count;
                        var maxCount = tickRevenue / Currency.OneCen;

                        if(count > maxCount)
                        {
                            var list = new List<long>(accounts);
                            list.Sort((a, b) => a.CompareTo(b));

                            var rand = new Lehmer(count);
                            for (var i = 0; i < count; i++)
                            {
                                var a = rand.Next(count);

                                var tmp = list[i];
                                list[i] = list[a];
                                list[a] = tmp;
                            }

                            for(var i = 0; i < maxCount; i++)
                            {
                                var accountId = list[i];
                                var account = GetMaintainAccount(accountId);
                                account.AddRevenue(tick, (int)Currency.OneCen);
                                commitItems.DirtyAccounts.Add(accountId);
                            }
                        }
                        else
                        {
                            foreach(var accountId in accounts)
                            {
                                var account = GetMaintainAccount(accountId);
                                account.AddRevenue(tick, accountRevenue);
                                commitItems.DirtyAccounts.Add(accountId);
                            }
                        }

                        previous = GetRevenueReceivers(previous.PreviousTick);
                    }

                    _revenueReceiversStroage.LastProcessedTick = last.PreviousTick;
                }
            }

            foreach (var accountId in commitItems.DirtyAccounts)
            {
                var account = GetMaintainAccount(accountId);
                _maintainAccountStorage.UpdateEntry(accountId, account.ToByteArray());
            }
        }

        protected override void ClearMetaData()
        {
            base.ClearMetaData();
            lock (_revenueLock)
                _revenueReceivers.Clear();

            _maintainAccounts.Clear();
        }

        internal MaintainAccount GetMaintainAccount(long accountId, bool newFromServiceChain = true)
        {
            if (_maintainAccounts.TryGetValue(accountId, out var account))
                return account;

            var data = _maintainAccountStorage.GetBlockData(accountId);
            if (data == null)
            {
                if (newFromServiceChain)
                {
                    var serviceAccount = ServiceChain.GetServiceAccount(accountId);
                    if (serviceAccount != null)
                    {
                        lock (_lock)
                        {
                            if (!_maintainAccounts.TryGetValue(accountId, out var maintainAccount))
                            {
                                if (ServiceChain.ServiceAccountExists(accountId))
                                {
                                    maintainAccount = new MaintainAccount(accountId);
                                    _maintainAccounts[accountId] = maintainAccount;
                                    _maintainAccountStorage.AddEntry(accountId, maintainAccount.ToByteArray());
                                }
                            }

                            return maintainAccount;
                        }
                    }
                }
                return null;
            }

            using (var unpacker = new Unpacker(data))
            {
                account = new MaintainAccount(unpacker);
                lock (_lock)
                {
                    if (_maintainAccounts.TryGetValue(accountId, out var storedAccount))
                        return storedAccount;

                    if (account.AccountId != accountId)
                        throw new Exception("Invalid data account data");

                    _maintainAccounts[accountId] = account;
                }

                return account;
            }
        }

        public bool GetAccountRevenueInfo(long accountId, out AccountRevenueInfo accountRevenueInfo)
        {
            var maintainAccount = GetMaintainAccount(accountId);
            var serviceAccount = ServiceChain.GetServiceAccount(accountId);

            if(maintainAccount != null && serviceAccount != null)
            {
                accountRevenueInfo = new AccountRevenueInfo(ChainId, maintainAccount.TotalRevenue, serviceAccount.TotalRevenuePayout, maintainAccount.GetLatestRevenus());
                return true;
            }

            accountRevenueInfo = null;
            return false;
        }

        RevenueReceivers GetCurrentRevenueReceivers()
        {
            lock(_revenueLock)
            {
                return GetRevenueReceivers(_revenueReceiversStroage.LastAvailableTick);
            }
        }

        RevenueReceivers GetRevenueReceivers(int tick)
        {
            lock (_revenueLock)
            {
                if (!_revenueReceivers.TryGetValue(tick, out var proposal))
                {
                    if (_revenueReceiversStroage.ContainsIndex(tick))
                    {
                        var data = _revenueReceiversStroage.GetBlockData(tick);
                        using (var unpacker = new Unpacker(data))
                        {
                            proposal = new RevenueReceivers(unpacker);
                            _revenueReceivers[tick] = proposal;
                        }
                    }
                }

                return proposal;
            }
        }

        public TransactionResultTypes ValidateMaintainTransaction(MaintainTransaction transaction)
        {
            if (transaction == null)
            {
                return TransactionResultTypes.InvalidTransaction;
            }

            var chainInfo = ServiceChain.CoreChain.GetChainInfo(ChainId);
            if(chainInfo == null)
            {
                return TransactionResultTypes.InvalidTransaction;
            }

            var chainKey = chainInfo.GetValidChainKey(transaction.ChainIndex, transaction.SignKeyIndex, transaction.Timestamp);
            if(!transaction.IsSignatureValid(chainKey?.PublicKey))
            {
                return TransactionResultTypes.InvaidChainKey;
            }

            var type = transaction.TransactionType;
            if(type == MainTainTransactionTypes.Revenue)
            {
                var rev = transaction as RevenueMaintainTransaction;
                var revenueInfo = rev.RevenueInfo;
                var tick = rev.Tick;

                var now = Protocol.TicksSinceGenesis(Time.Timestamp);

                if (tick < (now - 1) || tick > now)
                    return TransactionResultTypes.InvalidTransaction;

                if (revenueInfo == null)
                    return TransactionResultTypes.InvalidTransaction;

                if (revenueInfo != chainInfo.GetRevenueInfo(revenueInfo.Index))
                    return TransactionResultTypes.InvalidTransaction;

                if (revenueInfo.Revenue <= 0)
                    return TransactionResultTypes.InvalidTransaction;

                lock (_revenueLock)
                {
                    var previous = GetRevenueReceivers(tick - 1);
                    if(previous != null)
                    {
                        if (previous.Tick >= tick || previous.Tick != rev.PreviousTick)
                            return TransactionResultTypes.InvalidTransaction;
                    }

                    if (tick != now) // if from previous tick, we allow it only, if there are no revenues for current tick yet.
                    {
                        var r = GetRevenueReceivers(now);
                        if (r != null && r.Accounts.Count > 0)
                            return TransactionResultTypes.InvalidTransaction;
                    }

                    var revenues = GetRevenueReceivers(tick);
                    if (revenues != null)
                    {
                        foreach (var accountId in rev.Accounts)
                        {
                            if (revenues.Accounts.Contains(accountId))
                            {
                                return TransactionResultTypes.InvalidTransaction;
                            }
                        }
                    }

                    return TransactionResultTypes.Ok;
                }
            }

            return TransactionResultTypes.Unknown;
        }

        void ConsumeTransaction(DirtyData dirtyData, CommitItems commitItems, TransactionItem<MaintainTransaction> dataItem)
        {
            var transaction = dataItem.Transaction;
            var transactionId = transaction.TransactionId;

            if (transactionId <= LastProcessedTransactionId)
                return;

            if (transaction.TargetChainId == ChainId)
            {
                var type = transaction.TransactionType;

                if(type == MainTainTransactionTypes.Revenue)
                {
                    var rev = transaction as RevenueMaintainTransaction;
                    var revenueInfo = rev.RevenueInfo;
                    var tick = rev.Tick;

                    lock (_revenueLock)
                    {
                        var revenues = GetRevenueReceivers(tick);
                        if(revenues == null)
                        {
                            revenues = new RevenueReceivers(tick, rev.PreviousTick, revenueInfo);
                            _revenueReceivers[tick] = revenues;
                            _revenueReceiversStroage.AddEntry(tick, revenues.ToByteArray());
                        }

                        if (revenueInfo.Index > revenues.RevenueInfo.Index)
                            revenues.RevenueInfo = revenueInfo;

                        foreach (var aId in rev.Accounts)
                        {
                            revenues.Accounts.Add(aId);
                        }
                    }

                    dirtyData.DirtyRevenueTicks.Add(tick);
                }

                ConsumeTransactionFeatures(0, transaction, null, commitItems);
            }
        }

        public void ConsumeBlockData(BlockData<MaintainBlock> blockData)
        {
            if (!Active)
                return;

            var block = blockData.Block;
            if (block.BlockId <= LastProcessedBlockId)
                return;

            var dirtyData = new DirtyData();
            var commitItems = NewCommitItems();

            foreach (var item in block.Items)
            {
                ConsumeTransaction(dirtyData, commitItems, item);
                TransactionStorage.Add(block.BlockId, item);
            }

            CommmitDirtyData(dirtyData, commitItems);
            commitItems.Commit();

            var lastTransactionid = LastProcessedTransactionId;
            var count = block.Transactions.Count;
            if (count > 0)
            {
                lastTransactionid = block.Transactions[count - 1].TransactionId;
            }

            foreach (var metaStorage in _metaDiscStorage)
            {
                metaStorage.LastBlockId = block.BlockId;
                metaStorage.LastTransactionId = lastTransactionid;
                metaStorage.Commit();
            }

            TransactionStorage.Save();
        }

        public void ProposeAccountRevenue(long accountId, long timestamp)
        {
            var tick = Protocol.TicksSinceGenesis(timestamp);
            var now = Protocol.TicksSinceGenesis(Time.Timestamp);

            if (tick < (now - 1) || tick > (now + 1))
                return;

            lock (_revenueLock)
            {
                if (!_pendingRevenueProposals.TryGetValue(tick, out var pending))
                {
                    pending = new HashSet<long>();
                    _pendingRevenueProposals[tick] = pending;
                }

                pending.Add(accountId);
            }
        }

        public TransactionResultTypes PublishMaintainTransaction(MaintainTransaction transaction)
        {
            transaction.SignKey = ServiceChain.KeyStore.DecryptedKey;
            transaction.SignKeyIndex = ServiceChain.KeyStore.KeyIndex;

            var message = new NodeTransactionMessage(transaction) { SignKey = _node.NodeConfiguration.LocaleNodePrivateKey };
            // generate signatures
            message.ToByteArray();

            return _node.TransactionManager.AddNodeTransaction(message, null);
        }

        HashSet<long> GetProposedRevenueReceivers(int tick)
        {
            var commit = false;

            lock (_revenueLock)
            {
                if (!_storedRevenueProposals.TryGetValue(tick, out var proposal))
                {
                    if (_storedRevenueProposalsStorage.ContainsIndex(tick))
                    {
                        var data = _storedRevenueProposalsStorage.GetBlockData(tick);
                        using (var unpacker = new Unpacker(data))
                        {
                            proposal = unpacker.UnpackHashSetLong();
                            _storedRevenueProposals[tick] = proposal;
                        }
                    }
                    else
                    {
                        proposal = new HashSet<long>();
                        _storedRevenueProposals[tick] = proposal;
                        using (var packer = new Packer())
                        {
                            packer.Pack(proposal);
                            _storedRevenueProposalsStorage.AddEntry(tick, packer.ToByteArray());
                            commit = true;
                        }
                    }
                }

                if (commit)
                    _storedRevenueProposalsStorage.Commit();

                return proposal;
            }
        }

        async Task RevenueProposalLoop()
        {
            while (!_node.HasQuit)
            {
                var tick = Protocol.TicksSinceGenesis(Time.Timestamp);
                await Task.Delay(TimeSpan.FromSeconds(Protocol.RevenueProposalTimer));

                var currentRevenueInfo = ServiceChain.CoreChain.GetChainInfo(ChainId)?.CurrentRevenueInfo;
                if (currentRevenueInfo == null)
                    continue;

                RevenueMaintainTransaction transaction = null;

                lock (_revenueLock)
                {
                    var proposal = GetProposedRevenueReceivers(tick);

                    if(_pendingRevenueProposals.TryGetValue(tick, out var pending))
                    {
                        var commit = false;
                        foreach(var p in pending)
                        {
                            if (proposal.Add(p))
                                commit = true;
                        }

                        if (commit)
                        {
                            using (var packer = new Packer())
                            {
                                packer.Pack(proposal);
                                _storedRevenueProposalsStorage.UpdateEntry(tick, packer.ToByteArray());
                                _storedRevenueProposalsStorage.Commit();
                            }
                        }
                    }

                    var revenueReceivers = GetRevenueReceivers(tick);

                    if (proposal.Count > 0)
                    {
                        foreach(var accountId in proposal)
                        {
                            if (revenueReceivers != null && revenueReceivers.Accounts.Contains(accountId))
                                continue;

                            if (transaction == null)
                            {
                                var current = GetCurrentRevenueReceivers();
                                transaction = new RevenueMaintainTransaction(tick, current != null ? current.Tick : -1, currentRevenueInfo, ChainId);
                            }

                            transaction.Accounts.Add(accountId);
                        }
                    }
                }

                if (transaction != null)
                    PublishMaintainTransaction(transaction);
            }
        }
    }
}
