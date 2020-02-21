using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Chain.Blocks;
using Heleus.Chain.Core;
using Heleus.Chain.Data;
using Heleus.Chain.Storage;
using Heleus.Cryptography;
using Heleus.Operations;
using Heleus.Service;
using Heleus.Transactions;
using Heleus.Transactions.Features;

namespace Heleus.Chain.Service
{
    public sealed class ServiceChain : FeatureChain, IServiceChain
    {
        public readonly CoreChain CoreChain;

        readonly LazyLookupTable<long, ServiceAccount> _serviceAccounts = new LazyLookupTable<long, ServiceAccount> { LifeSpan = TimeSpan.FromMinutes(30), Depth = 4 };
        MetaDiscStorage _serviceAccountStorage => _metaDiscStorage[0];

        bool _chainKeyValid;
        readonly Key _chainKey;
        readonly short _chainKeyIndex;

        internal ChainKeyStore KeyStore;

        public ServiceChain(int chainId, Node.Node node, CoreChain coreChain, ChainKeyStore keyStore) : base(ChainType.Service, chainId, 0, node)
        {
            CoreChain = coreChain;

            var endPoints = new HashSet<AvailableEndPoint>();

            _chainKeyIndex = keyStore.KeyIndex;
            _chainKey = keyStore.DecryptedKey;
            KeyStore = keyStore;

            var chainInfo = CoreChain.GetChainInfo(ChainId);
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

            AvailableEndpoints = new List<AvailableEndPoint>(endPoints);
        }

        protected override bool NewMetaStorage()
        {
            try
            {
                _metaDiscStorage.Add(new MetaDiscStorage(_storage, ChainType, ChainId, ChainIndex, "serviceaccounts", 256, DiscStorageFlags.UnsortedDynamicIndex | DiscStorageFlags.AppendOnly));
                return base.NewMetaStorage();
            }
            catch { }
            return false;
        }

        protected override void BuildMetaData(TransactionDiscStorage transactionStorage, long sliceIndex)
        {
            var commitItems = NewCommitItems();
            Hash previousHash = null; // Todo, get hash from previous storage

            for (var transactionId = transactionStorage.StartIndex; transactionId <= transactionStorage.EndIndex; transactionId++)
            {
                if (transactionId <= LastProcessedTransactionId)
                    continue;

                var transactionData = transactionStorage.GetBlockDataRawIndex(transactionId);
                var item = new TransactionItem<ServiceTransaction>(transactionData);

                if (previousHash != null)
                {
                    if (!item.Validation.IsValid(previousHash, new ArraySegment<byte>(transactionData, 0, transactionData.Length - ValidationOperation.ValidationOperationDataSize)))
                    {
                        throw new Exception("Transaction Validation failed");
                    }
                }

                previousHash = item.Validation.Hash;

                ConsumeTransaction(commitItems, item);
            }

            CommitDirtyAccounts(commitItems);
            commitItems.Commit();
        }

        void CommitDirtyAccounts(CommitItems commitItems)
        {
            foreach (var accountId in commitItems.DirtyAccounts)
            {
                var account = GetServiceAccount(accountId);
                _serviceAccountStorage.UpdateEntry(accountId, account.ToByteArray());
            }
        }

        protected override void ClearMetaData()
        {
            base.ClearMetaData();
            _serviceAccounts.Clear();
        }

        public bool ServiceAccountExists(long accountId)
        {
            if (_serviceAccounts.Contains(accountId))
                return true;

            return _serviceAccountStorage.ContainsIndex(accountId);
        }

        internal ServiceAccount GetServiceAccount(long accountId)
        {
            if (_serviceAccounts.TryGetValue(accountId, out var account))
                return account;

            var data = _serviceAccountStorage.GetBlockData(accountId);
            if (data == null)
                return null;

            using (var unpacker = new Unpacker(data))
            {
                account = new ServiceAccount(unpacker);
                lock (_lock)
                {
                    if (_serviceAccounts.TryGetValue(accountId, out var storedAccount))
                        return storedAccount;

                    if (account.AccountId != accountId)
                        throw new Exception("Invalid service account data");

                    _serviceAccounts[accountId] = account;
                }

                return account;
            }
        }

        public bool GetRevokealbeServiceAccountKey(long accountId, Key publicKey, out bool isValidAccount, out RevokeablePublicServiceAccountKey revokeableKey)
        {
            var account = GetServiceAccount(accountId);
            isValidAccount = account != null;

            if (isValidAccount)
            {
                revokeableKey = account.GetAccountKey(publicKey);
                return revokeableKey != null;
            }

            revokeableKey = null;
            return false;
        }

        public PublicServiceAccountKey GetValidServiceAccountKey(long accountId, short keyIndex, long timestamp)
        {
            var account = GetServiceAccount(accountId);
            var key = account?.GetAccountKey(keyIndex);
            if (key != null)
            {
                if (key.IsExpired(timestamp))
                    return null;
            }
            return key;
        }

        public RevokeablePublicServiceAccountKey GetRevokealbeServiceAccountKey(long accountId, short keyIndex)
        {
            var account = GetServiceAccount(accountId);
            return account?.GetRevokableAccountKey(keyIndex);
        }

        public bool GetNextServiceAccountKeyIndex(long accountId, out short nextKeyIndex)
        {
            var account = GetServiceAccount(accountId);
            if (account != null)
            {
                nextKeyIndex = account.AccountKeyCount;
                return true;
            }

            nextKeyIndex = 0;
            return CoreChain.CoreAccountExists(accountId);
        }

        void ConsumeTransaction(CommitItems commitItems, TransactionItem<ServiceTransaction> item)
        {
            var transaction = item.Transaction;

            if (transaction.TransactionId <= LastProcessedTransactionId)
                return;

            if (transaction.TargetChainId == ChainId)
            {
                var type = transaction.TransactionType;

                var accountId = transaction.AccountId;
                var account = GetServiceAccount(accountId);

                if (type == ServiceTransactionTypes.Join)
                {
                    var joinTransaction = transaction as JoinServiceTransaction;
                    if (account == null)
                    {
                        account = new ServiceAccount(accountId, ChainId, joinTransaction.Timestamp);
                        if (joinTransaction.AccountKey != null)
                            account.AddAccountKey(joinTransaction.AccountKey, joinTransaction.Timestamp);

                        lock (_lock)
                            _serviceAccounts[accountId] = account;

                        _serviceAccountStorage.AddEntry(accountId, account.ToByteArray());

                        return; // skip dirty account stuff
                    }

                    if (joinTransaction.AccountKey != null)
                    {
                        account.AddAccountKey(joinTransaction.AccountKey, joinTransaction.Timestamp);
                        commitItems.DirtyAccounts.Add(accountId);
                    }
                }
                else if (type == ServiceTransactionTypes.Purchase)
                {
                    var purchaseTransaction = transaction as PurchaseServiceTransaction;
                    var purchase = CoreChain.GetChainInfo(ChainId).GetPurchase(purchaseTransaction.PurchaseGroupId, purchaseTransaction.PurchaseItemId);

                    if (purchase != null)
                    {
                        var targetAccount = GetServiceAccount(purchaseTransaction.ReceiverAccountId);
                        if (targetAccount != null && purchase != null)
                        {
                            targetAccount.AddPurchase(purchaseTransaction, purchase);
                            commitItems.DirtyAccounts.Add(targetAccount.AccountId);
                        }
                    }
                }
                else if (type == ServiceTransactionTypes.RequestRevenue)
                {
                    var revenueTransaction = transaction as RequestRevenueServiceTransaction;
                    account.UpdateTotelRevenuePayout(revenueTransaction.CurrentTotalRevenuePayout + revenueTransaction.PayoutAmount);
                }

                ConsumeTransactionFeatures((ushort)ServiceTransactionTypes.FeatureRequest, transaction, account, commitItems);

                commitItems.DirtyAccounts.Add(accountId);
            }
        }

        public async Task<TransactionValidationResult> ValidateServiceTransaction(ServiceTransaction transaction)
        {
            var userCode = 0L;
            var result = TransactionResultTypes.Unknown;
            var message = string.Empty;
            TransactionValidation nodeValidation = null;

            if (transaction == null)
            {
                result = TransactionResultTypes.InvalidTransaction;
                goto end;
            }

            var chainInfo = CoreChain.GetChainInfo(ChainId);
            var accountId = transaction.AccountId;
            var service = _node.ChainManager.GetService(ChainId);

            if (chainInfo == null)
            {
                result = TransactionResultTypes.ChainNotFound;
                goto end;
            }

            if (service == null)
            {
                result = TransactionResultTypes.ChainServiceUnavailable;
                goto end;
            }

            if (!_chainKeyValid)
            {
                var chainKey = chainInfo.GetValidChainKey(ChainIndex, _chainKeyIndex, Time.Timestamp);
                if (chainKey == null || chainKey.PublicKey != _chainKey.PublicKey)
                {
                    result = TransactionResultTypes.ChainNodeInvalid;
                    goto end;
                }
                _chainKeyValid = true;
            }

            if (BlockStorage.History.ContainsTransactionIdentifier(transaction))
            {
                result = TransactionResultTypes.AlreadyProcessed;
                goto end;
            }

            var coreAccount = CoreChain.GetCoreAccount(accountId);
            if (coreAccount == null)
            {
                result = TransactionResultTypes.InvalidCoreAccount;
                goto end;
            }

            {
                var (featResult, featCode) = ValidateTransactionFeatures((ushort)ServiceTransactionTypes.FeatureRequest, transaction);
                if (featResult != TransactionResultTypes.Ok)
                {
                    result = featResult;
                    userCode = featCode;

                    goto end;
                }
            }

            var validation = await service.IsServiceTransactionValid(transaction);
            userCode = validation.UserCode;
            message = validation.Message;

            if (!validation.IsOK)
            {
                if (validation.Result == ServiceResultTypes.PurchaseRequired)
                    result = TransactionResultTypes.PurchaseRequired;
                else
                    result = TransactionResultTypes.ChainServiceErrorResponse;
                goto end;
            }

            result = TransactionResultTypes.Ok;
            nodeValidation = new TransactionValidation(transaction, _chainKeyIndex, _chainKey);
        end:

            return new TransactionValidationResult(result, userCode, message, nodeValidation);
        }

        public void ConsumeBlockData(BlockData<ServiceBlock> blockData)
        {
            if (!Active)
                return;

            var block = blockData.Block;
            if (block.BlockId <= LastProcessedBlockId)
                return;

            var commitItems = NewCommitItems();

            foreach (var item in block.Items)
            {
                ConsumeTransaction(commitItems, item);
                TransactionStorage.Add(block.BlockId, item);
            }

            CommitDirtyAccounts(commitItems);
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

        // IServiceChain
        public TransactionItem<ServiceTransaction> GetTransactionItem(long transactionId)
        {
            return TransactionStorage.GetTransactionItem<ServiceTransaction>(transactionId);
        }

        // IFeatureChain
        public override FeatureAccount GetFeatureAccount(long accountId)
        {
            return GetServiceAccount(accountId);
        }

        public override bool FeatureAccountExists(long accountId)
        {
            return ServiceAccountExists(accountId);
        }
    }
}
