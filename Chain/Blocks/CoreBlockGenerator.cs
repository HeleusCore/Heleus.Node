using System;
using System.Collections.Generic;
using System.Linq;
using Heleus.Base;
using Heleus.Chain.Core;
using Heleus.Chain.Purchases;
using Heleus.Cryptography;
using Heleus.Operations;
using Heleus.Transactions;

namespace Heleus.Chain.Blocks
{
    public partial class CoreBlockGenerator : BlockGenerator
    {
        readonly CoreChain _coreChain;
        readonly Dictionary<long, CoreAccount> _accounts = new Dictionary<long, CoreAccount>();
        readonly Dictionary<int, ChainInfo> _chains = new Dictionary<int, ChainInfo>();

        readonly CoreBlockInfo _blockInfo = new CoreBlockInfo();

        readonly BlockState _blockState;
        readonly CoreBlock _lastBlock;

        public long NextAccountId { get; private set; }
        public int NextChainId { get; private set; }

        public bool IsValid
        {
            get
            {
                if (!_blockInfo.HasTransaction)
                    return false;

                if (_lastBlock != null)
                    return _lastBlock.BlockId == _coreChain.LastProcessedBlockId && _lastBlock.BlockId == _blockState.BlockId;

                return _coreChain.LastProcessedBlockId == Protocol.InvalidBlockId;
            }
        }

        public CoreBlockGenerator(CoreChain coreChain, CoreBlock lastBlock)
        {
            _coreChain = coreChain;
            _lastBlock = lastBlock;
            _blockState = coreChain.GetChainInfo(Protocol.CoreChainId).LastState;

            NextAccountId = lastBlock.NextAccountId;
            NextChainId = lastBlock.NextChainId;
        }

        CoreAccount GetCoreAccount(long accountId)
        {
            if (_accounts.TryGetValue(accountId, out var account))
                return account;

            account = _coreChain.GetCoreAccount(accountId, true, true);
            if (account == null)
                return null;

            _accounts[account.AccountId] = account;
            return account;
        }

        ChainInfo GetChainInfo(int chainId)
        {
            if (_chains.TryGetValue(chainId, out var chainInfo))
                return chainInfo;

            chainInfo = _coreChain.GetChainInfo(chainId, true, true);
            if (chainInfo == null)
                return null;

            _chains[chainId] = chainInfo;
            return chainInfo;
        }

        public TransactionResultTypes ConsumeTransaction(CoreTransaction transaction)
        {
            return ConsumeTransaction(transaction, false);
        }

        TransactionResultTypes CheckChainKey(int chainId, short keyIndex, bool adminRequierd, ref Key key)
        {
            return CheckChainKey(GetChainInfo(chainId), keyIndex, adminRequierd, ref key);
        }

        TransactionResultTypes CheckChainKey(ChainInfo chain, short keyIndex, bool adminRequired, ref Key key)
        {
            if (chain == null)
                return TransactionResultTypes.ChainNotFound;
            var chainKey = chain.GetChainKey(keyIndex);
            if (chainKey == null)
                return TransactionResultTypes.ChainKeyNotFound;
            if (chainKey.IsExpired())
                return TransactionResultTypes.ChainKeyExpired;
            if (adminRequired && (chainKey.Flags & PublicChainKeyFlags.ChainAdminKey) == 0)
                return TransactionResultTypes.ChainKeyNotFound;

            key = chainKey.PublicKey;
            return TransactionResultTypes.Ok;
        }

        TransactionResultTypes ConsumeTransaction(CoreTransaction transaction, bool addExtraTime)
        {
            if (transaction.IsExpired(addExtraTime))
                return TransactionResultTypes.Expired;

            var r = _coreChain.BlockStorage.HistoryContainsTransactionOrRegistration(transaction);
            if (r != TransactionResultTypes.Ok)
                return r;

            var coreType = transaction.TransactionType;

            if (coreType != CoreTransactionTypes.AccountRegistration)
            {
                var account = GetCoreAccount(transaction.AccountId);
                if (account == null)
                    return TransactionResultTypes.InvalidCoreAccount;

                var key = account.AccountKey;

                if (coreType == CoreTransactionTypes.ChainUpdate)
                {
                    var update = transaction as ChainUpdateCoreTransaction;
                    var chain = GetChainInfo(update.ChainId);
                    var keyIndex = update.SignKeyIndex;

                    if (keyIndex != Protocol.CoreAccountSignKeyIndex)
                    {
                        var result = CheckChainKey(update.ChainId, keyIndex, true, ref key);
                        if (result != TransactionResultTypes.Ok)
                            return result;
                    }
                    else if (transaction.AccountId != chain.AccountId)
                    {
                        return TransactionResultTypes.InvalidCoreAccount;
                    }
                }
                else if (coreType == CoreTransactionTypes.ServiceBlock)
                {
                    var serviceBlockTransaction = transaction as ServiceBlockCoreTransaction;
                    var block = serviceBlockTransaction.ServiceBlock;
                    key = _coreChain.GetValidPublicChainKeyWithFlags(block.ChainId, block.ChainIndex, block.Issuer, PublicChainKeyFlags.ServiceChainVoteKey, transaction.Timestamp)?.PublicKey;
                }

                if (!transaction.IsSignatureValid(key))
                    return TransactionResultTypes.InvalidSignature;
            }
            else
            {
                var register = transaction as AccountRegistrationCoreTransaction;
                if (!transaction.IsSignatureValid(register.PublicKey))
                    return TransactionResultTypes.InvalidSignature;
            }

            if (coreType == CoreTransactionTypes.AccountRegistration)
            {
                var registration = transaction as AccountRegistrationCoreTransaction;
                var account = new CoreAccount(NextAccountId, registration.PublicKey);
                _accounts[account.AccountId] = account;
                NextAccountId++;

                _blockInfo.AddNewAccount(registration, account);

                return TransactionResultTypes.Ok;
            }

            if (coreType == CoreTransactionTypes.ChainRegistration || coreType == CoreTransactionTypes.ChainUpdate)
            {
                var update = transaction as ChainUpdateCoreTransaction;
                var registration = transaction as ChainRegistrationCoreTransaction;
                var isUpdate = update != null;

                var account = GetCoreAccount(transaction.AccountId);
                if (account == null)
                    return TransactionResultTypes.InvalidCoreAccount;

                ChainInfo chain = null;
                if (isUpdate)
                {
                    chain = GetChainInfo(update.ChainId);
                    if (chain == null)
                        return TransactionResultTypes.ChainNotFound;
                }

                if (!registration.ChainWebsite.IsValdiUrl())
                    return TransactionResultTypes.InvalidChainWebsite;

                if (registration.ChainName != null && registration.ChainName.Length > ChainRegistrationCoreTransaction.MaxNameLength)
                    return TransactionResultTypes.InvalidChainName;

                foreach (var endPoint in registration.PublicEndpoints)
                {
                    if (!endPoint.IsValdiUrl(false))
                        return TransactionResultTypes.InvalidChainEndpoint;
                }

                var indices = new HashSet<short>();
                foreach (var chainKey in registration.ChainKeys)
                {
                    if (indices.Contains(chainKey.KeyIndex))
                        return TransactionResultTypes.InvaidChainKey;

                    indices.Add(chainKey.KeyIndex);
                }

                foreach (var purchase in registration.Purchases)
                {
                    if (purchase.PurchaseType == PurchaseTypes.None)
                        return TransactionResultTypes.InvalidChainPurchase;

                    foreach (var p in registration.Purchases)
                    {
                        if (purchase == p)
                            continue;

                        if (p.PurchaseItemId == purchase.PurchaseItemId && p.PurchaseGroupId == purchase.PurchaseGroupId)
                            return TransactionResultTypes.InvalidChainPurchase;

                        if (p.PurchaseGroupId == purchase.PurchaseGroupId && p.PurchaseType != purchase.PurchaseType)
                            return TransactionResultTypes.InvalidChainPurchase;
                    }
                }

                if (!isUpdate)
                {
                    chain = new ChainInfo(NextChainId, registration.AccountId, registration.ChainName, registration.ChainWebsite);
                    var operation = new ChainInfoOperation(chain.ChainId, chain.AccountId, chain.Name, chain.Website, registration.Timestamp, registration.ChainKeys, registration.PublicEndpoints, registration.Purchases);

                    _chains[chain.ChainId] = chain;
                    NextChainId++;

                    _blockInfo.AddChainUpdate(registration, operation);
                }
                else
                {
                    var result = chain.IsUpdateValid(update);
                    if (result != TransactionResultTypes.Ok)
                        return result;

                    var operation = new ChainInfoOperation(chain.ChainId, update.AccountId, update.ChainName, update.ChainWebsite, registration.Timestamp, update.ChainKeys, update.PublicEndpoints, update.Purchases, update.RevokeChainKeys, update.RemovePublicEndPoints, update.RemovePurchaseItems);
                    _blockInfo.AddChainUpdate(update, operation);
                }

                return TransactionResultTypes.Ok;
            }

            if (coreType == CoreTransactionTypes.Transfer)
            {
                var transfer = transaction as TransferCoreTransaction;

                if (!AccountUpdateOperation.IsReasonValid(transfer.Reason))
                    return TransactionResultTypes.InvalidTransferReason;

                if (transfer.AccountId == transfer.ReceiverAccountId)
                    return TransactionResultTypes.InvalidReceiverAccount;

                var sender = GetCoreAccount(transfer.AccountId);
                var receiver = GetCoreAccount(transfer.ReceiverAccountId);
                if (receiver == null)
                    return TransactionResultTypes.InvalidReceiverAccount;

                if (sender != null)
                {
                    var amount = transfer.Amount;
                    if (sender.CanTransfer(amount))
                    {
                        sender.RemoveFromTranfser(amount);
                        receiver.AddFromTransfer(amount);

                        _blockInfo.AddTransfer(transfer);
                        return TransactionResultTypes.Ok;
                    }
                    return TransactionResultTypes.InsuficientBalance;
                }
                return TransactionResultTypes.InvalidTransaction;
            }

            if (coreType == CoreTransactionTypes.ServiceBlock)
            {
                var serviceBlockTransaction = (transaction as ServiceBlockCoreTransaction);
                var block = serviceBlockTransaction.ServiceBlock;

                var chainInfo = GetChainInfo(block.ChainId);
                if (chainInfo == null)
                    return TransactionResultTypes.ChainNotFound;

                if (block.TransactionCount == 0)
                    return TransactionResultTypes.InvalidTransaction;

                var state = chainInfo.LastState;
                if ((state.BlockId + 1) != block.BlockId)
                    return TransactionResultTypes.InvalidBlock;

                foreach (var serviceTransaction in block.Transactions)
                {
                    var serviceType = serviceTransaction.TransactionType;
                    var accountId = serviceTransaction.AccountId;
                    var account = GetCoreAccount(accountId);

                    if (account == null)
                        return TransactionResultTypes.InvalidCoreAccount;

                    if (!serviceTransaction.IsSignatureValid(account.AccountKey))
                        return TransactionResultTypes.InvalidSignature;

                    if (serviceType == ServiceTransactionTypes.Join)
                    {
                        var joinTransaction = serviceTransaction as JoinServiceTransaction;
                        var key = joinTransaction.AccountKey;

                        if (key == null || (key != null && key.IsKeySignatureValid(account.AccountKey)))
                            continue;

                        return TransactionResultTypes.InvalidSignature;
                    }

                    if (serviceType == ServiceTransactionTypes.Purchase)
                    {
                        var purchaseTransaction = serviceTransaction as PurchaseServiceTransaction;

                        var buyer = GetCoreAccount(purchaseTransaction.AccountId);
                        var receiver = GetCoreAccount(purchaseTransaction.ReceiverAccountId);
                        if (buyer != null && receiver != null && chainInfo.IsPurchaseValid(purchaseTransaction.PurchaseGroupId, purchaseTransaction.PurchaseItemId, purchaseTransaction.Price))
                        {
                            var chainAccount = GetCoreAccount(chainInfo.AccountId);
                            if (chainAccount != null && buyer.CanPurchase(purchaseTransaction.Price))
                            {
                                //buyer.Purchase(purchaseTransaction.Price, chainAccount);
                                continue;
                            }
                            return TransactionResultTypes.CannotPurchase;
                        }

                        return TransactionResultTypes.InvalidCoreAccount;
                    }

                    if(serviceType == ServiceTransactionTypes.RequestRevenue)
                    {
                        var totalRevenue = chainInfo.GetTotalAccountRevenue(transaction.Timestamp);
                        var totalPayout = chainInfo.TotalAccountPayout;
                        var availablePayout = totalRevenue - totalPayout;

                        var revenueTransaction = serviceTransaction as RequestRevenueServiceTransaction;

                        if (availablePayout >= revenueTransaction.PayoutAmount)
                        {
                            continue;
                        }
                    }

                    return TransactionResultTypes.InvalidTransaction;
                }

                if (!_coreChain.IsBlockProposalSignatureValid(block, serviceBlockTransaction.ProposalSignatures))
                    return TransactionResultTypes.InvalidBlockSignature;

                _blockInfo.AddServiceBlock(serviceBlockTransaction);
                return TransactionResultTypes.Ok;
            }

            return TransactionResultTypes.InvalidTransaction;
        }

        public HashSet<long> CheckBlock(CoreBlock block)
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
                            invalid.Add(block.Items[i].Transaction.OperationId);
                    }
                }

                if (invalid.Count <= 0)
                    invalid.Add(Protocol.InvalidBlockId);
            }

            return invalid;
        }

        public CoreBlock GenerateBlock(short issuer, int revision)
        {
            return GenerateBlock(issuer, revision, Time.Timestamp);
        }

        CoreBlock GenerateBlock(short issuer, int revision, long timestamp)
        {
            if (_blockInfo.ServiceBlocks.Count > 0)
            {
                var removed = new HashSet<int>();

                foreach (var serviceBlockTransaction in _blockInfo.ServiceBlocks.Values)
                {
                    var chainInfo = GetChainInfo(serviceBlockTransaction.ServiceBlock.ChainId);

                    var totalRevenue = chainInfo.GetTotalAccountRevenue(timestamp);
                    var totalPayout = chainInfo.TotalAccountPayout;
                    var availablePayout = totalRevenue - totalPayout;

                    var invalidBalance = false;
                    foreach (var serviceTransaction in serviceBlockTransaction.ServiceBlock.Transactions)
                    {
                        if (serviceTransaction.TransactionType == ServiceTransactionTypes.Purchase)
                        {
                            var purchaseTransaction = serviceTransaction as PurchaseServiceTransaction;
                            var buyer = GetCoreAccount(purchaseTransaction.AccountId);

                            if (!buyer.CanPurchase(purchaseTransaction.Price))
                            {
                                invalidBalance = true;
                                removed.Add(serviceBlockTransaction.ServiceBlock.ChainId);
                                break;
                            }
                        }
                        else if (serviceTransaction.TransactionType == ServiceTransactionTypes.RequestRevenue)
                        {
                            var revenue = serviceTransaction as RequestRevenueServiceTransaction;

                            availablePayout -= revenue.PayoutAmount;

                            if (availablePayout < 0)
                            {
                                invalidBalance = true;
                                break;
                            }
                        }
                    }

                    if (!invalidBalance)
                    {
                        var serviceAccount = GetCoreAccount(chainInfo.AccountId);

                        foreach (var serviceTransaction in serviceBlockTransaction.ServiceBlock.Transactions)
                        {
                            if (serviceTransaction.TransactionType == ServiceTransactionTypes.Purchase)
                            {
                                var purchaseTransaction = serviceTransaction as PurchaseServiceTransaction;
                                var buyer = GetCoreAccount(purchaseTransaction.AccountId);

                                buyer.Purchase(purchaseTransaction.Price, serviceAccount);

                                _blockInfo.AccountUpdates.Purchases.Add(purchaseTransaction);
                                _blockInfo.AccountUpdates.AffectedAccounts.Add(serviceTransaction.AccountId);
                                _blockInfo.AccountUpdates.AffectedAccounts.Add(purchaseTransaction.ReceiverAccountId);
                                _blockInfo.AccountUpdates.AffectedAccounts.Add(chainInfo.AccountId);
                            }
                            else if (serviceTransaction.TransactionType == ServiceTransactionTypes.Join)
                            {
                                _blockInfo.AccountUpdates.Joins.Add(serviceTransaction as JoinServiceTransaction);
                                _blockInfo.AccountUpdates.AffectedAccounts.Add(serviceTransaction.AccountId);
                            }
                            else if (serviceTransaction.TransactionType == ServiceTransactionTypes.RequestRevenue)
                            {
                                var revenue = serviceTransaction as RequestRevenueServiceTransaction;
                                var networkAccount = GetCoreAccount(CoreAccount.NetworkAccountId);
                                var account = GetCoreAccount(serviceTransaction.AccountId);

                                networkAccount.RemoveFromTranfser(revenue.PayoutAmount);
                                account.AddFromTransfer(revenue.PayoutAmount);

                                _blockInfo.AccountUpdates.Revenues.Add(revenue);
                                _blockInfo.AccountUpdates.AffectedAccounts.Add(serviceTransaction.AccountId);
                                _blockInfo.AccountUpdates.AffectedAccounts.Add(CoreAccount.NetworkAccountId);
                            }
                        }
                    }
                }

                foreach (var remove in removed)
                    _blockInfo.ServiceBlocks.Remove(remove);
            }

            if (!_blockInfo.HasTransaction || !(_coreChain.LastProcessedBlockId == _lastBlock.BlockId) || !(_blockState.BlockId == _lastBlock.BlockId))
                return null;

            var coreChain = GetChainInfo(CoreChain.CoreChainId);

            var state = coreChain.LastState;
            var nextCoreTransactionId = state.LastTransactionId + 1;
            var blockId = _lastBlock.BlockId + 1;

            _blockInfo.Sort();

            var coreOperations = new List<CoreOperation>();
            var coreTransactions = new List<CoreTransaction>();

            foreach (var newAccount in _blockInfo.NewAccounts)
            {
                newAccount.Operation.UpdateOperationId(nextCoreTransactionId);
                newAccount.Transaction.MetaData.SetTransactionId(nextCoreTransactionId);

                var account = GetCoreAccount(newAccount.Operation.AccountId);
                account.LastTransactionId = nextCoreTransactionId;

                ++nextCoreTransactionId;
                coreOperations.Add(newAccount.Operation);
                coreTransactions.Add(newAccount.Transaction);
            }

            foreach (var newChain in _blockInfo.Chains)
            {
                newChain.Operation.UpdateOperationId(nextCoreTransactionId);
                newChain.Transaction.MetaData.SetTransactionId(nextCoreTransactionId);

                var account = GetCoreAccount(newChain.Operation.AccountId);
                newChain.Operation.PreviousAccountTransactionId = account.LastTransactionId;
                account.LastTransactionId = nextCoreTransactionId;

                ++nextCoreTransactionId;
                coreOperations.Add(newChain.Operation);
                coreTransactions.Add(newChain.Transaction);
            }

            if (_blockInfo.AccountUpdates.HasUpdates)
            {
                _blockInfo.AccountUpdates.Operation.UpdateOperationId(nextCoreTransactionId);

                foreach (var accountId in _blockInfo.AccountUpdates.AffectedAccounts)
                {
                    var account = GetCoreAccount(accountId);
                    _blockInfo.AccountUpdates.Operation.AddAccount(accountId, account.LastTransactionId, account.HeleusCoins);

                    account.LastTransactionId = nextCoreTransactionId;
                }

                foreach (var transaction in _blockInfo.AccountUpdates.Transactions)
                {
                    _blockInfo.AccountUpdates.Operation.AddTransfer(transaction.AccountId, transaction.ReceiverAccountId, transaction.Amount, transaction.Reason, transaction.Timestamp);
                    transaction.MetaData.SetTransactionId(nextCoreTransactionId);

                    coreTransactions.Add(transaction);
                }

                foreach (var transaction in _blockInfo.AccountUpdates.Purchases)
                {
                    _blockInfo.AccountUpdates.Operation.AddPurchase(transaction.AccountId, transaction.Price, transaction.TargetChainId, transaction.PurchaseItemId, transaction.Timestamp);
                }

                foreach (var transaction in _blockInfo.AccountUpdates.Joins)
                {
                    _blockInfo.AccountUpdates.Operation.AddJoin(transaction.AccountId, transaction.TargetChainId, transaction.AccountKey != null ? transaction.AccountKey.KeyIndex : Protocol.CoreAccountSignKeyIndex, transaction.Timestamp);
                }

                foreach(var transaction in _blockInfo.AccountUpdates.Revenues)
                {
                    _blockInfo.AccountUpdates.Operation.AddRevenue(transaction.AccountId, transaction.TargetChainId, transaction.PayoutAmount, transaction.Timestamp);
                }

                ++nextCoreTransactionId;
                coreOperations.Add(_blockInfo.AccountUpdates.Operation);
            }

            var blockStateOperation = new BlockStateOperation(timestamp);
            coreOperations.Add(blockStateOperation);
            blockStateOperation.UpdateOperationId(nextCoreTransactionId);
            blockStateOperation.AddBlockState(Protocol.CoreChainId, blockId, issuer, revision, nextCoreTransactionId);
            ++nextCoreTransactionId;

            foreach (var serviceBlockTransaction in _blockInfo.ServiceBlocks.Values)
            {
                blockStateOperation.AddBlockState(serviceBlockTransaction.ServiceBlock);
                coreTransactions.Add(serviceBlockTransaction);
            }

            var lastOperationHash = _lastBlock.Items.Last().Validation.Hash;
            var block = new CoreBlock(blockId, issuer, revision, timestamp, NextAccountId, NextChainId, _lastBlock.BlockHash, lastOperationHash, coreOperations, coreTransactions);

            // return a copy of the block and all its transactions
            // if we don't do this, transaction ids might change during another generator run and we get invalid ids
            return Block.Restore<CoreBlock>(block.BlockData);
        }
    }
}
