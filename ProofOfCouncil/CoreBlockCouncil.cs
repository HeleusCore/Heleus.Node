using System.Collections.Generic;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Chain.Blocks;
using Heleus.Cryptography;
using Heleus.Messages;
using Heleus.Transactions;

namespace Heleus.ProofOfCouncil
{
    public class CoreBlockCouncil : Council<CoreBlock>
    {
        public CoreBlockCouncil(Node.Node node, short keyIndex, Key key) : base(node, Chain.ChainType.Core, Protocol.CoreChainId, 0, keyIndex, key)
        {
        }

        Task NewBlock(BlockEvent<CoreBlock> blockEvent)
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
            _node.PubSub.Subscribe<BlockEvent<CoreBlock>>(this, NewBlock);

            return base.Start();
        }

        public override void Stop()
        {
            _node.PubSub.Unsubscribe<BlockEvent<CoreBlock>>(this);

            base.Stop();
        }

        public override CoreBlock GetBlockProposal(VoteProcess<CoreBlock> voteProcess, CoreBlock lastBlock, int revision)
        {
            var remove = new HashSet<long>();
            var generator = new CoreBlockGenerator(_coreChain, lastBlock);

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

                    if (generator.ConsumeTransaction(transaction as CoreTransaction) != TransactionResultTypes.Ok)
                    {
                        remove.Add(transaction.UniqueIdentifier);
                    }
                }

                foreach (var item in remove)
                    _transactions.Remove(item);
            }

            return generator.GenerateBlock(voteProcess.LocalIssuer, revision);
        }

        public override bool CheckBlockProposal(VoteProcess<CoreBlock> voteProcess, CoreBlock block, CoreBlock lastBlock, out HashSet<long> invalidTransactionIds)
        {
            var generator = new CoreBlockGenerator(_coreChain, lastBlock);
            invalidTransactionIds = generator.CheckBlock(block);

            if (invalidTransactionIds.Count > 0)
                return false;

            return true;
        }

        public override void NewBlockAvailable(VoteProcess<CoreBlock> voteProcess, BlockData<CoreBlock> blockData, BlockProposalSignatures blockProposalSignatures)
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
