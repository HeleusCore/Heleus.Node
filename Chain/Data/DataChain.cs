using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Chain.Blocks;
using Heleus.Chain.Core;
using Heleus.Chain.Purchases;
using Heleus.Chain.Service;
using Heleus.Chain.Storage;
using Heleus.Cryptography;
using Heleus.Manager;
using Heleus.Operations;
using Heleus.Service;
using Heleus.Transactions;
using Heleus.Transactions.Features;

namespace Heleus.Chain.Data
{
    public sealed class DataChain : FeatureChain, IDataChain
    {
        readonly CoreChain _coreChain;
        readonly ServiceChain _serviceChain;

        readonly LazyLookupTable<long, DataAccount> _dataAccounts = new LazyLookupTable<long, DataAccount> { LifeSpan = TimeSpan.FromMinutes(30), Depth = 4 };

        public readonly AttachementManager Attachements;

        bool _chainKeyValid;
        public readonly Key ChainKey;
        public readonly short ChainKeyIndex;
        internal ChainKeyStore KeyStore;


        public readonly int AttachementKey;

        MetaDiscStorage _dataAccountStorage => _metaDiscStorage[0];

        public DataChain(int chainId, uint chainIndex, Node.Node node, CoreChain coreChain, ServiceChain serviceChain, ChainKeyStore keyStore, int attachementKey) : base(ChainType.Data, chainId, chainIndex, node)
        {
            _coreChain = coreChain;
            _serviceChain = serviceChain;
            Attachements = node.AttachementManager;

            ChainKeyIndex = keyStore.KeyIndex;
            ChainKey = keyStore.DecryptedKey;
            KeyStore = keyStore;

            AttachementKey = attachementKey;

            var endPoints = new HashSet<AvailableEndPoint>();

            var chainInfo = _coreChain.GetChainInfo(ChainId);
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
                _metaDiscStorage.Add(new MetaDiscStorage(_storage, ChainType, ChainId, ChainIndex, "dataaccounts", 256, DiscStorageFlags.UnsortedDynamicIndex | DiscStorageFlags.AppendOnly));

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
            var commitItems = NewCommitItems();
            Hash previousHash = null; // Todo, get hash from previous storage

            for (var transactionId = transactionStorage.StartIndex; transactionId <= transactionStorage.EndIndex; transactionId++)
            {
                if (transactionId <= LastProcessedTransactionId)
                    continue;

                var transactionData = transactionStorage.GetBlockDataRawIndex(transactionId);
                var item = new TransactionItem<DataTransaction>(transactionData);

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

            CommmitDirtyAccounts(commitItems);
            commitItems.Commit();
        }

        void CommmitDirtyAccounts(CommitItems commitItems)
        {
            foreach (var accountId in commitItems.DirtyAccounts)
            {
                var account = GetDataAccount(accountId);
                _dataAccountStorage.UpdateEntry(accountId, account.ToByteArray());
            }
        }

        protected override void ClearMetaData()
        {
            base.ClearMetaData();
            _dataAccounts.Clear();
        }

        internal DataAccount GetDataAccount(long accountId, bool newFromServiceChain = true)
        {
            if (_dataAccounts.TryGetValue(accountId, out var account))
                return account;

            var data = _dataAccountStorage.GetBlockData(accountId);
            if (data == null)
            {
                if (newFromServiceChain)
                {
                    var serviceAccount = _serviceChain.GetServiceAccount(accountId);
                    if (serviceAccount != null)
                    {
                        lock (_lock)
                        {
                            if (!_dataAccounts.TryGetValue(accountId, out var dataAccount))
                            {
                                if (_serviceChain.ServiceAccountExists(accountId))
                                {
                                    dataAccount = new DataAccount(accountId);
                                    _dataAccounts[accountId] = dataAccount;
                                    _dataAccountStorage.AddEntry(accountId, dataAccount.ToByteArray());

                                    //TaskRunner.Run(_dataAccountStorage.Commit);
                                }
                            }

                            return dataAccount;
                        }
                    }
                }
                return null;
            }

            using (var unpacker = new Unpacker(data))
            {
                account = new DataAccount(unpacker);
                lock (_lock)
                {
                    if (_dataAccounts.TryGetValue(accountId, out var storedAccount))
                        return storedAccount;

                    if (account.AccountId != accountId)
                        throw new Exception("Invalid data account data");

                    _dataAccounts[accountId] = account;
                }

                return account;
            }
        }

        void ConsumeTransaction(CommitItems commitItems, TransactionItem<DataTransaction> dataItem)
        {
            var transaction = dataItem.Transaction;
            var transactionId = transaction.TransactionId;

            if (transactionId <= LastProcessedTransactionId)
                return;

            if (transaction.TargetChainId == ChainId)
            {
                var accountId = transaction.AccountId;
                var account = GetDataAccount(accountId);

                var type = transaction.TransactionType;

                if (type == DataTransactionTypes.Attachement)
                {
                    var attTransaction = transaction as AttachementDataTransaction;
                    Attachements.StoreAttachements(attTransaction);
                }

                ConsumeTransactionFeatures((ushort)DataTransactionTypes.FeatureRequest, transaction, account, commitItems);
            }
        }

        public async Task<TransactionValidationResult> ValidateDataTransaction(DataTransaction transaction)
        {
            var userCode = 0L;
            var result = TransactionResultTypes.Unknown;
            var message = string.Empty;
            TransactionValidation nodeValidation = null;

            var type = transaction.TransactionType;
            var chainInfo = _coreChain.GetChainInfo(ChainId);
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
                var chainKey = chainInfo.GetValidChainKey(ChainIndex, ChainKeyIndex, Time.Timestamp);
                if (chainKey == null || chainKey.PublicKey != ChainKey.PublicKey)
                {
                    result = TransactionResultTypes.ChainNodeInvalid;
                    goto end;
                }
                _chainKeyValid = true;
            }

            if (transaction == null)
            {
                result = TransactionResultTypes.InvalidTransaction;
                goto end;
            }

            if (!transaction.IsDataTransactionValid)
            {
                result = TransactionResultTypes.InvalidContent;
                goto end;
            }

            if (BlockStorage.History.ContainsTransactionIdentifier(transaction))
            {
                result = TransactionResultTypes.AlreadyProcessed;
                goto end;
            }

            var coreAccount = _coreChain.GetCoreAccount(accountId);
            if (coreAccount == null)
            {
                result = TransactionResultTypes.InvalidCoreAccount;
                goto end;
            }

            var serviceAccount = _serviceChain.GetServiceAccount(accountId);

            if (transaction.SignKeyIndex == Protocol.CoreAccountSignKeyIndex)
            {
                if (!transaction.IsSignatureValid(coreAccount.AccountKey, null))
                {
                    result = TransactionResultTypes.InvalidSignature;
                    goto end;
                }
            }
            else
            {
                if (serviceAccount == null)
                {
                    result = TransactionResultTypes.InvalidServiceAccount;
                    goto end;
                }

                var accountKey = serviceAccount.GetValidAccountKey(transaction.SignKeyIndex, transaction.Timestamp);
                if (accountKey == null)
                {
                    result = TransactionResultTypes.InvalidServiceAccountKey;
                    goto end;
                }

                if (accountKey.IsExpired())
                {
                    result = TransactionResultTypes.Expired;
                    goto end;
                }

                if (!transaction.IsSignatureValid(coreAccount.AccountKey, accountKey))
                {
                    result = TransactionResultTypes.InvalidSignature;
                    goto end;
                }
            }

            {
                var (featResult, featCode) = ValidateTransactionFeatures((ushort)DataTransactionTypes.FeatureRequest, transaction);
                if (featResult != TransactionResultTypes.Ok)
                {
                    result = featResult;
                    userCode = featCode;
                    goto end;
                }
            }

            if (type == DataTransactionTypes.Attachement)
            {
                if (!Attachements.AreAttachementsUploaded(transaction as AttachementDataTransaction))
                {
                    result = TransactionResultTypes.AttachementsNotUploaded;
                    goto end;
                }
            }

            var purchaseRequired = transaction.GetFeature<RequiredPurchase>(RequiredPurchase.FeatureId);
            if (purchaseRequired != null && purchaseRequired.RequiredPurchaseType != PurchaseTypes.None)
            {
                if (!serviceAccount.HasRequiredTransactionPurchase(transaction, purchaseRequired))
                {
                    result = TransactionResultTypes.PurchaseRequired;
                    goto end;
                }
            }

            var validation = await service.IsDataTransactionValid(transaction);
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
            nodeValidation = new TransactionValidation(transaction, ChainKeyIndex, ChainKey);
        end:

            return new TransactionValidationResult(result, userCode, message, nodeValidation);
        }

        public void ConsumeBlockData(BlockData<DataBlock> blockData)
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

            CommmitDirtyAccounts(commitItems);
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

        // IDataChain
        public TransactionItem<DataTransaction> GetTransactionItem(long transactionId)
        {
            return TransactionStorage.GetTransactionItem<DataTransaction>(transactionId);
        }

        public string GetLocalAttachementPath(long transactionId, int attachementKey, string name)
        {
            return Path.Combine(_storage.Root.FullName, AttachementManager.GetAttachementPath(ChainId, ChainIndex, attachementKey), AttachementManager.GetAttachementFileName(transactionId, name));
        }

        public Task<byte[]> GetLocalAttachementData(long transactionId, int attachementKey, string name)
        {
            var path = Path.Combine(AttachementManager.GetAttachementPath(ChainId, ChainIndex, attachementKey), AttachementManager.GetAttachementFileName(transactionId, name));
            return _storage.ReadFileBytesAsync(path);
        }

        // IFeatureChain
        public override FeatureAccount GetFeatureAccount(long accountId)
        {
            return GetDataAccount(accountId);
        }

        public override bool FeatureAccountExists(long accountId)
        {
            return _serviceChain.ServiceAccountExists(accountId);
        }
    }
}
