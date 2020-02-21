using System.Collections.Generic;
using System.Linq;
using Heleus.Base;
using Heleus.Chain.Core;
using Heleus.Chain.Maintain;
using Heleus.Chain.Service;
using Heleus.Cryptography;
using Heleus.Operations;
using Heleus.Transactions;

namespace Heleus.Chain.Blocks
{
    public partial class ServiceBlockGenerator : BlockGenerator
    {
        public readonly int ChainId;

        readonly CoreChain _coreChain;
        readonly ServiceChain _serviceChain;
        readonly MaintainChain _maintainChain;
        readonly ServiceBlock _lastBlock;

        readonly BlockState _blockState;
        readonly ChainInfo _chainInfo;

        readonly ServiceBlockInfo _blockInfo;

        public bool IsValid
        {
            get
            {
                if (!_blockInfo.HasTransaction)
                    return false;

                if (_lastBlock != null)
                    return _lastBlock.BlockId == _serviceChain.LastProcessedBlockId && _lastBlock.BlockId == _blockState.BlockId;

                return _serviceChain.LastProcessedBlockId == Protocol.InvalidBlockId;
            }
        }

        public ServiceBlockGenerator(CoreChain coreChain, ServiceChain serviceChain, MaintainChain maintainChain, ServiceBlock lastBlock)
        {
            ChainId = serviceChain.ChainId;

            _blockInfo = new ServiceBlockInfo(serviceChain);
            _coreChain = coreChain;
            _serviceChain = serviceChain;
            _maintainChain = maintainChain;
            _lastBlock = lastBlock;
            _chainInfo = coreChain.GetChainInfo(ChainId);
            _blockState = _chainInfo.LastState;
        }

        public static TransactionResultTypes IsJoinValid(CoreAccount coreAccount, ServiceAccount chainAccount, JoinServiceTransaction transaction)
        {
            if (coreAccount == null)
                return TransactionResultTypes.InvalidTransaction;

            if (transaction == null)
                return TransactionResultTypes.InvalidTransaction;

            if (!transaction.IsSignatureValid(coreAccount.AccountKey))
            {
                return TransactionResultTypes.InvalidSignature;
            }

            var chainKey = transaction.AccountKey;
            if (chainKey == null && chainAccount != null)
            {
                return TransactionResultTypes.AlreadyJoined;
            }

            if (chainKey != null && chainAccount != null)
            {
                if (chainAccount.ContainsAccountKeyKey(chainKey))
                {
                    return TransactionResultTypes.AlreadyJoined;
                }

                if (chainAccount.HasAccountKeyIndex(chainKey.KeyIndex))
                {
                    return TransactionResultTypes.InvalidServiceAccountKey;
                }

                if (chainKey.KeyIndex != chainAccount.AccountKeyCount)
                {
                    return TransactionResultTypes.InvalidServiceAccountKey;
                }
            }

            return TransactionResultTypes.Ok;
        }

        public static TransactionResultTypes IsPurchaseValid(CoreAccount payer, ServiceAccount receiver, ChainInfo chainInfo, PurchaseServiceTransaction transaction)
        {
            if (chainInfo == null)
            {
                return TransactionResultTypes.ChainNotFound;
            }

            if (payer == null || !payer.CanPurchase(transaction.Price))
            {
                return TransactionResultTypes.InsuficientBalance;
            }

            if (receiver == null)
            {
                return TransactionResultTypes.InvalidServiceAccount;
            }

            if (!chainInfo.IsPurchaseValid(transaction.PurchaseGroupId, transaction.PurchaseItemId, transaction.Price))
            {
                return TransactionResultTypes.PurchaseNotFound;
            }

            if (!receiver.CanPurchaseItem(transaction, chainInfo))
                return TransactionResultTypes.CannotPurchase;

            return TransactionResultTypes.Ok;
        }

        public TransactionResultTypes ConsumeTransaction(ServiceTransaction transaction, bool addExtraTime = false)
        {
            if (transaction.IsExpired(addExtraTime))
                return TransactionResultTypes.Expired;

            var transactionResult = _serviceChain.BlockStorage.HistoryContainsTransactionOrRegistration(transaction);
            if (transactionResult != TransactionResultTypes.Ok)
                return transactionResult;

            var accountId = transaction.AccountId;
            var coreAccount = _coreChain.GetCoreAccount(accountId);
            var serviceAccount = _serviceChain.GetServiceAccount(accountId);

            var type = transaction.TransactionType;
            switch (type)
            {
                case ServiceTransactionTypes.Join:

                    var joinTransaction = (transaction as JoinServiceTransaction);
                    var joinResult = IsJoinValid(coreAccount, serviceAccount, joinTransaction);
                    if (joinResult == TransactionResultTypes.Ok)
                    {
                        if (_blockInfo.AddJoin(joinTransaction, serviceAccount, _serviceChain))
                            return TransactionResultTypes.Ok;

                        return TransactionResultTypes.BlockLimitExceeded;
                    }

                    return joinResult;

                case ServiceTransactionTypes.Purchase:

                    if (serviceAccount == null)
                        return TransactionResultTypes.InvalidServiceAccount;

                    var purchaseTransaction = (transaction as PurchaseServiceTransaction);
                    var receiver = _serviceChain.GetServiceAccount(purchaseTransaction.ReceiverAccountId);
                    var purchaseResult = IsPurchaseValid(coreAccount, receiver, _chainInfo, purchaseTransaction);
                    if (purchaseResult == TransactionResultTypes.Ok)
                    {
                        if (_blockInfo.AddPurchase(purchaseTransaction, serviceAccount, _serviceChain))
                            return TransactionResultTypes.Ok;

                        return TransactionResultTypes.BlockLimitExceeded;
                    }

                    return purchaseResult;

                case ServiceTransactionTypes.RequestRevenue:

                    if (serviceAccount == null)
                        return TransactionResultTypes.InvalidServiceAccount;

                    var maintainAccount = _maintainChain.GetMaintainAccount(accountId);
                    if(maintainAccount == null)
                        return TransactionResultTypes.InvalidServiceAccount;

                    var revenueTransaction = transaction as RequestRevenueServiceTransaction;

                    if(serviceAccount.TotalRevenuePayout != revenueTransaction.CurrentTotalRevenuePayout)
                    {
                        return TransactionResultTypes.RevenueAmoutInvalid;
                    }

                    if(revenueTransaction.PayoutAmount <= 0)
                    {
                        return TransactionResultTypes.RevenueAmoutInvalid;
                    }

                    var totalPayout = serviceAccount.TotalRevenuePayout + revenueTransaction.PayoutAmount;
                    if(totalPayout > maintainAccount.TotalRevenue)
                    {
                        return TransactionResultTypes.RevenueAmoutInvalid;
                    }

                    if (_blockInfo.AddRevenue(revenueTransaction, serviceAccount, _serviceChain))
                        return TransactionResultTypes.Ok;

                    return TransactionResultTypes.BlockLimitExceeded;

                case ServiceTransactionTypes.FeatureRequest:
                case ServiceTransactionTypes.Service:
                    return TransactionResultTypes.Ok;
            }

            return TransactionResultTypes.InvalidTransaction;
        }

        public HashSet<long> CheckBlock(ServiceBlock block)
        {
            var invalid = new HashSet<long>();
            foreach (var transaction in block.Transactions)
            {
                var result = ConsumeTransaction(transaction, true); // add some extra time when checking blocks
                if (result != TransactionResultTypes.Ok)
                    invalid.Add(transaction.TransactionId);
            }

            var newBlock = GenerateBlock(block.Issuer, block.Revision, block.Timestamp);
            if (newBlock == null || newBlock.BlockHash != block.BlockHash)
            {
                if (newBlock != null)
                {
                    var count = newBlock.Items.Count;
                    for (var i = 0; i < count; i++)
                    {
                        var op1 = newBlock.Items[i].Validation;
                        var op2 = block.Items[i].Validation;

                        if (op1.Hash != op2.Hash)
                            invalid.Add(block.Items[i].Transaction.TransactionId);
                    }
                }

                if (invalid.Count <= 0)
                    invalid.Add(Protocol.InvalidBlockId);
            }

            return invalid;
        }

        public ServiceBlock GenerateBlock(short issuer, int revision)
        {
            return GenerateBlock(issuer, revision, Time.Timestamp);
        }

        public ServiceBlock GenerateBlock(short issuer, int revision, long timestamp)
        {
            if (!IsValid)
                return null;

            var nextTransactionId = Operation.FirstTransactionId;
            var blockId = Protocol.GenesisBlockId;
            var previousHash = Hash.Empty(Protocol.TransactionHashType);
            var lastTransactionHash = Hash.Empty(ValidationOperation.ValidationHashType);

            if (_lastBlock != null)
            {
                blockId = _lastBlock.BlockId + 1;
                nextTransactionId = _blockState.LastTransactionId + 1;
                previousHash = _lastBlock.BlockHash;
                lastTransactionHash = _lastBlock.Items.Last().Validation.Hash;
            }

            _blockInfo.Sort();

            var transactions = new List<ServiceTransaction>();
            foreach (var transaction in _blockInfo.Transactions)
            {
                var accountId = transaction.AccountId;

                transaction.MetaData.SetTransactionId(nextTransactionId);

                transactions.Add(transaction);

                _blockInfo.FeatureGenerator.ProcessTransaction(transaction);

                ++nextTransactionId;
            }

            var block = new ServiceBlock(transactions, Protocol.Version, blockId, ChainId, issuer, revision, timestamp, previousHash, lastTransactionHash);

            // return a copy of the block and all its transactions
            // if we don't do this, transaction ids might change during another generator run and we get invalid ids
            return Block.Restore<ServiceBlock>(block.BlockData);
        }
    }
}
