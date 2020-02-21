using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Heleus.Chain.Blocks;
using Heleus.Chain.Maintain;
using Heleus.Chain.Service;
using Heleus.Cryptography;
using Heleus.Messages;
using Heleus.Transactions;

namespace Heleus.ProofOfCouncil
{
    public class ServiceBlockCouncil : Council<ServiceBlock>
    {
        readonly ServiceChain _serviceChain;
        readonly MaintainChain _maintainChain;
        readonly long _chainAccountId;

        public ServiceBlockCouncil(Node.Node node, int chainId, ServiceChain serviceChain, MaintainChain maintainChain, short keyIndex, Key key) : base(node, Chain.ChainType.Service, chainId, 0, keyIndex, key)
        {
            _serviceChain = serviceChain;
            _maintainChain = maintainChain;

            var chainInfo = node.ChainManager.CoreChain.GetChainInfo(chainId);
            if (chainInfo != null)
                _chainAccountId = chainInfo.AccountId;
        }

        Task NewBlock(BlockEvent<ServiceBlock> blockEvent)
        {
            var block = blockEvent.Block;
            if (IsValidBlockType(block))
            {
                NewVoteProcess(block, 0);

                lock (_lock)
                {
                    foreach (var transaction in block.Transactions)
                        _transactions.Remove(transaction.UniqueIdentifier);
                }
            }

            return Task.CompletedTask;
        }

        async Task NewBlock(BlockEvent<CoreBlock> blockEvent)
        {
            await CheckBlockProposals();
        }

        async Task CheckBlockProposals()
        {
            var chainInfo = _coreChain.GetChainInfo(ChainId);
            var lastState = chainInfo.LastState;
            var lastProcessedBlockId = _serviceChain.LastProcessedBlockId;

            if (lastState.BlockId > lastProcessedBlockId)
            {
                var lastStates = chainInfo.GetLastBlockStates();
                for (var i = 0; i < lastStates.Length; i++)
                {
                    lastState = lastStates[i];
                    if (lastState.BlockId > lastProcessedBlockId)
                    {
                        var proposal = GetBlockProposal(lastState.BlockId, lastState.Issuer, lastState.Revision);
                        if (proposal != null)
                        {
                            await _node.SyncManager.HandleBlockData(proposal, null);
                        }
                    }
                }
            }
        }

        public override async Task Start()
        {
            _node.PubSub.Subscribe<BlockEvent<ServiceBlock>>(this, NewBlock);
            _node.PubSub.Subscribe<BlockEvent<CoreBlock>>(this, NewBlock);

            await base.Start();
            await CheckBlockProposals();
        }

        public override void Stop()
        {
            _node.PubSub.Unsubscribe<BlockEvent<ServiceBlock>>(this);
            _node.PubSub.Unsubscribe<BlockEvent<CoreBlock>>(this);

            base.Stop();
        }

        public override bool CheckBlockProposal(VoteProcess<ServiceBlock> voteProcess, ServiceBlock block, ServiceBlock lastBlock, out HashSet<long> invalidTransactionIds)
        {
            var generator = new ServiceBlockGenerator(_coreChain, _serviceChain, _maintainChain, lastBlock);
            invalidTransactionIds = generator.CheckBlock(block);

            if (invalidTransactionIds.Count > 0)
                return false;

            return true;
        }

        public override ServiceBlock GetBlockProposal(VoteProcess<ServiceBlock> voteProcess, ServiceBlock lastBlock, int revision)
        {
            var remove = new HashSet<long>();
            var generator = new ServiceBlockGenerator(_coreChain, _serviceChain, _maintainChain, lastBlock);

            lock (_lock)
            {
                foreach (var item in _transactions)
                {
                    var transaction = item.Value;
                    if (_blockStorage.HistoryContainsTransactionOrRegistration(transaction) != TransactionResultTypes.Ok)
                    {
                        remove.Add(transaction.UniqueIdentifier);
                        continue;
                    }

                    if (generator.ConsumeTransaction(transaction as ServiceTransaction) != TransactionResultTypes.Ok)
                    {
                        remove.Add(transaction.UniqueIdentifier);
                    }
                }

                foreach (var item in remove)
                    _transactions.Remove(item);
            }

            return generator.GenerateBlock(voteProcess.LocalIssuer, revision);
        }

        public override void NewBlockAvailable(VoteProcess<ServiceBlock> voteProcess, BlockData<ServiceBlock> blockData, BlockProposalSignatures blockProposalSignatures)
        {
            base.NewBlockAvailable(voteProcess, blockData, blockProposalSignatures);

            var block = blockData.Block;
            if (block.Issuer == LocalKeyIndex)
            {
                var message = new NodeTransactionMessage(new ServiceBlockCoreTransaction(blockData.Block, blockProposalSignatures, _chainAccountId) { SignKey = _localKey });
                message.ToByteArray();

                _node.TransactionManager.AddNodeTransaction(message, null);
                _ = _node.NodeServer.Broadcast(message, null);
            }
        }
    }
}
