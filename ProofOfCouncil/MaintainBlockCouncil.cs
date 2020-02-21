using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Chain;
using Heleus.Chain.Blocks;
using Heleus.Chain.Maintain;
using Heleus.Cryptography;
using Heleus.Messages;
using Heleus.Transactions;

namespace Heleus.ProofOfCouncil
{
    public sealed class MaintainBlockCouncil : Council<MaintainBlock>
    {
        readonly MaintainChain _maintainChain;

        public MaintainBlockCouncil(Node.Node node, MaintainChain maintainChain, short keyIndex, Key key) : base(node, ChainType.Maintain, maintainChain.ChainId, maintainChain.ChainIndex, keyIndex, key)
        {
            _maintainChain = maintainChain;
        }

        Task NewBlock(BlockEvent<MaintainBlock> blockEvent)
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

        public override Task Start()
        {
            _node.PubSub.Subscribe<BlockEvent<MaintainBlock>>(this, NewBlock);

            return base.Start();
        }

        public override void Stop()
        {
            _node.PubSub.Unsubscribe<BlockEvent<MaintainBlock>>(this);

            base.Stop();
        }

        public override bool CheckBlockProposal(VoteProcess<MaintainBlock> voteProcess, MaintainBlock block, MaintainBlock lastBlock, out HashSet<long> invalidTransactionIds)
        {
            var generator = new MaintainBlockGenerator(_maintainChain, lastBlock);
            invalidTransactionIds = generator.CheckBlock(block);

            if (invalidTransactionIds.Count > 0)
                return false;

            return true;
        }

        public override MaintainBlock GetBlockProposal(VoteProcess<MaintainBlock> voteProcess, MaintainBlock lastBlock, int revision)
        {
            var remove = new HashSet<long>();
            var generator = new MaintainBlockGenerator(_maintainChain, lastBlock);

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

                    try
                    {
                        if (generator.ConsumeTransaction(transaction as MaintainTransaction) != TransactionResultTypes.Ok)
                        {
                            remove.Add(transaction.UniqueIdentifier);
                        }
                    }
                    catch (Exception ex)
                    {
                        remove.Add(transaction.UniqueIdentifier);
                        Log.HandleException(ex, this);
                    }
                }

                foreach (var item in remove)
                    _transactions.Remove(item);
            }

            return generator.GenerateBlock(voteProcess.LocalIssuer, revision);
        }

        public override void NewBlockAvailable(VoteProcess<MaintainBlock> voteProcess, BlockData<MaintainBlock> blockData, BlockProposalSignatures blockProposalSignatures)
        {
            base.NewBlockAvailable(voteProcess, blockData, blockProposalSignatures);

            var block = blockData.Block;
            TaskRunner.Run(async () =>
            {
                await _node.SyncManager.HandleBlockData(blockData, GetVoteConnections());
                await _node.NodeServer.Broadcast(new NodeBlockDataMessage(block.ChainType, block.BlockId, block.ChainId, block.ChainIndex, _node.NodeConfiguration.LocaleNodeInfo.IsPublicEndPoint ? null : blockData) { SignKey = _node.NodeConfiguration.LocaleNodePrivateKey }, null);
            });
        }
    }
}
