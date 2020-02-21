using System;
using System.Collections.Generic;
using System.Linq;
using Heleus.Base;
using Heleus.Chain.Maintain;
using Heleus.Cryptography;
using Heleus.Operations;
using Heleus.Transactions;
using Heleus.Transactions.Features;

namespace Heleus.Chain.Blocks
{
    public class MaintainBlockGenerator : BlockGenerator
    {
        readonly MaintainChain _maintainChain;
        readonly MaintainBlock _lastBlock;

        readonly List<MaintainTransaction> _transactions = new List<MaintainTransaction>();
        readonly BlockTransactionGenerator _featureGenerator;

        public bool IsValid
        {
            get
            {
                if (_transactions.Count == 0)
                    return false;

                if (_lastBlock != null)
                    return _lastBlock.BlockId == _maintainChain.LastProcessedBlockId;

                return _maintainChain.LastProcessedBlockId == Protocol.InvalidBlockId;
            }
        }

        public MaintainBlockGenerator(MaintainChain maintainChain, MaintainBlock lastBlock)
        {
            _maintainChain = maintainChain;
            _lastBlock = lastBlock;
            _featureGenerator = new BlockTransactionGenerator(maintainChain);
        }

        public TransactionResultTypes ConsumeTransaction(MaintainTransaction transaction, bool addExtraTime = false)
        {
            if (transaction.IsExpired(addExtraTime))
                return TransactionResultTypes.Expired;

            var transactionResult = _maintainChain.BlockStorage.HistoryContainsTransactionOrRegistration(transaction);
            if (transactionResult != TransactionResultTypes.Ok)
                return transactionResult;

            var type = transaction.TransactionType;
            switch (type)
            {
                case MainTainTransactionTypes.Maintain:
                case MainTainTransactionTypes.Revenue:

                    var (result, _, _) = _featureGenerator.AddTransaction(BlockTransactionGeneratorMode.Preprocess, _maintainChain, transaction, null);
                    if (result == TransactionResultTypes.Ok)
                        _transactions.Add(transaction);

                    return result;
            }

            return TransactionResultTypes.InvalidTransaction;
        }

        public HashSet<long> CheckBlock(MaintainBlock block)
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

        public MaintainBlock GenerateBlock(short issuer, int revision)
        {
            return GenerateBlock(issuer, revision, Time.Timestamp);
        }

        MaintainBlock GenerateBlock(short issuer, int revision, long timestamp)
        {
            if (!IsValid)
                return null;

            var nextTransactionId = Operation.FirstTransactionId;
            var nextBlockId = Protocol.GenesisBlockId;
            var previousHash = Hash.Empty(Protocol.TransactionHashType);
            var lastTransactionHash = Hash.Empty(ValidationOperation.ValidationHashType);

            var blockTransactions = new List<MaintainTransaction>();

            if (_lastBlock != null)
            {
                nextBlockId = _lastBlock.BlockId + 1;
                nextTransactionId = _lastBlock.LastTransactionId + 1;
                previousHash = _lastBlock.BlockHash;
                lastTransactionHash = _lastBlock.Items.Last().Validation.Hash;
            }

            _transactions.Sort((a, b) => a.UniqueIdentifier.CompareTo(b.UniqueIdentifier));

            foreach (var transaction in _transactions)
            {
                transaction.MetaData.SetTransactionId(nextTransactionId);
                _featureGenerator.ProcessTransaction(transaction);

                nextTransactionId++;
                blockTransactions.Add(transaction);
            }

            if (blockTransactions.Count == 0)
                return null;

            var block = new MaintainBlock(Protocol.Version, _maintainChain.ChainId, nextBlockId, issuer, revision, timestamp, previousHash, lastTransactionHash, blockTransactions);

            // return a copy of the block and all its transactions
            // if we don't do this, transaction ids might change during another generator run and we get invalid ids
            return Block.Restore<MaintainBlock>(block.BlockData);
        }
    }
}
