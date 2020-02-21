using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Chain;
using Heleus.Chain.Blocks;
using Heleus.Chain.Core;
using Heleus.Chain.Storage;
using Heleus.Cryptography;
using Heleus.Messages;
using Heleus.Network;
using Heleus.Transactions;

namespace Heleus.ProofOfCouncil
{
    public abstract class Council : ILogger
    {
        public readonly ChainType ChainType;
        public readonly int ChainId;
        public readonly uint ChainIndex;

        public readonly PublicChainKeyFlags RequiresChainVoteKeyFlags;
        public readonly PublicChainKeyFlags RequiresChainKeyFlags;

        readonly public short LocalKeyIndex;
        readonly protected Key _localKey;

        public string LogName => GetType().Name;

        readonly protected HashSet<NodeConnection> _voteConnections = new HashSet<NodeConnection>();
        protected HashSet<NodeConnection> GetVoteConnections()
        {
            lock (_lock)
                return new HashSet<NodeConnection>(_voteConnections);
        }

        readonly protected object _lock = new object();

        readonly protected Node.Node _node;
        readonly protected CoreChain _coreChain;
        readonly protected BlockStorage _blockStorage;
        readonly protected Dictionary<long, Transaction> _transactions = new Dictionary<long, Transaction>();

        public abstract Task OnMessage(CouncilMessage message, NodeConnection connection);

        protected Council(Node.Node node, ChainType chainType, int chainId, uint chainIndex, short keyIndex, Key key)
        {
            ChainType = chainType;
            ChainId = chainId;
            ChainIndex = chainIndex;

            RequiresChainVoteKeyFlags = Block.GetRequiredChainVoteKeyFlags(ChainType);
            RequiresChainKeyFlags = Block.GetRequiredChainKeyFlags(ChainType);
            LocalKeyIndex = keyIndex;
            _localKey = key;

            _node = node;
            _coreChain = node.ChainManager.CoreChain;
            _blockStorage = node.ChainManager.GetChain(ChainType, ChainId, chainIndex).BlockStorage;
        }

        public abstract Task Start();
        public abstract void Stop();

        public bool NewTransaction(Transaction transaction, NodeConnection connection)
        {
            if (transaction == null || transaction.TargetChainId != ChainId)
                return false;

            if (_blockStorage.HistoryContainsTransactionOrRegistration(transaction) != TransactionResultTypes.Ok)
                return false;

            lock (_lock)
            {
                if (_transactions.ContainsKey(transaction.UniqueIdentifier))
                    return false;

                _transactions[transaction.UniqueIdentifier] = transaction;
            }

            // broadcast to other members, if it's not from a member
            if (connection == null)
            {
                _ = Broadcast(new NodeTransactionMessage(transaction, 0) { SignKey = _node.NodeConfiguration.LocaleNodePrivateKey }, null);
            }
            else
            {
                lock (_lock)
                {
                    if (!_voteConnections.Contains(connection))
                        _ = Broadcast(new NodeTransactionMessage(transaction, 0) { SignKey = _node.NodeConfiguration.LocaleNodePrivateKey }, connection.NodeInfo.NodeId);
                }
            }

            return true;
        }

        protected Task Broadcast(Message message, Hash ignoredNodeId)
        {
            message.ToByteArray(true);

            lock (_lock)
            {
                foreach (var connection in _voteConnections)
                {
                    if (connection.NodeInfo.NodeId == ignoredNodeId)
                        continue;

                    try
                    {
                        TaskRunner.Run(() => connection.Send(message));
                    }
                    catch (Exception ex)
                    {
                        Log.IgnoreException(ex, this);
                    }
                }
            }

            return Task.CompletedTask;
        }

        public void AddVoteNodeConnection(NodeConnection connection)
        {
            lock (_lock)
            {
                _voteConnections.Add(connection);
            }

            // force sync
            var blockData = _blockStorage.LastBlockData;
            if (blockData != null)
                _ = connection.Send(new NodeBlockDataMessage(blockData.ChainType, blockData.BlockId, blockData.ChainId, blockData.ChainIndex, blockData) { SignKey = _node.NodeConfiguration.LocaleNodePrivateKey });
        }

        public void RemoveVoteNodeConnection(NodeConnection connection)
        {
            lock (_lock)
            {
                _voteConnections.Remove(connection);
            }
        }
    }

    public abstract class Council<BlockType> : Council, IVoteProcessHandler<BlockType> where BlockType : Block
    {
        readonly string _blockProposalPath;

        readonly Storage _storage;
        SortedList<long, List<BlockData<BlockType>>> _proposals = new SortedList<long, List<BlockData<BlockType>>>();
        VoteProcess<BlockType> _voteProcess;

        protected Council(Node.Node node, ChainType chainType, int chainId, uint chainIndex, short keyIndex, Key key) : base(node, chainType, chainId, chainIndex, keyIndex, key)
        {
            _storage = node.Storage;

            _blockProposalPath = GetBlockProposalPath(chainType, ChainId, ChainIndex);
            _storage.CreateDirectory(_blockProposalPath);
        }

        protected bool IsValidBlockType(BlockType block)
        {
            if (block == null)
                return false;

            return (block.ChainType == ChainType && block.ChainId == ChainId && ChainIndex == block.ChainIndex);
        }

        public override Task Start()
        {
            _proposals = LoadStoredBlockProposals(_storage, ChainType, ChainId, ChainIndex);

            NewVoteProcess(_blockStorage.LastBlock as BlockType, 0);

            return Task.CompletedTask;
        }

        public override void Stop()
        {
            lock (_lock)
            {
                _voteProcess?.Cancel();
                _voteProcess = null;
            }
        }

        public static string GetBlockProposalFileName(BlockType block)
        {
            return $"{block.BlockId}_{block.Issuer}_{block.Revision}.block";
        }

        public static string GetBlockProposalPath(ChainType chainType, int chainId, uint chainIndex)
        {
            return $"temp/blockproposals/{chainType.ToString().ToLower()}/{chainId}_{chainIndex}/";
        }

        public BlockData<BlockType> GetBlockProposal(long blockId, short issuer, int revision)
        {
            lock (_lock)
            {
                if (_proposals.TryGetValue(blockId, out var blocks))
                {
                    foreach (var blockData in blocks)
                    {
                        var block = blockData.Block;
                        if (block.Issuer == issuer && block.Revision == revision)
                        {
                            return blockData;
                        }
                    }
                }
            }

            return null;
        }

        void RemoveOldBlockProposals(BlockType block)
        {
            Task.Run(() =>
            {
                var removeProposals = new List<BlockData<BlockType>>();
                lock (_lock)
                {
                    var removeKeys = new List<long>();
                    var keys = _proposals.Keys;
                    foreach (var key in keys)
                    {
                        if (key < block.BlockId - 10)
                        {
                            removeKeys.Add(key);
                        }
                    }

                    if (removeKeys.Count > 0)
                    {
                        foreach (var key in removeKeys)
                        {
                            removeProposals.AddRange(_proposals[key]);
                            _proposals.Remove(key);
                        }
                    }
                }

                foreach (var item in removeProposals)
                {
                    _storage.DeleteFile(Path.Combine(_blockProposalPath, GetBlockProposalFileName(item.Block)));
                }
            });
        }

        static SortedList<long, List<BlockData<BlockType>>> LoadStoredBlockProposals(Storage storage, ChainType chainType, int chainId, uint chainIndex)
        {
            var result = new SortedList<long, List<BlockData<BlockType>>>();

            var files = storage.GetFiles(GetBlockProposalPath(chainType, chainId, chainIndex), "*.block");
            foreach (var file in files)
            {
                try
                {
                    var parts = file.Name.Split('_');
                    var blockId = long.Parse(parts[0]);
                    var issuer = short.Parse(parts[1]);
                    var revision = int.Parse(parts[2].Remove(parts[2].IndexOf('.')));

                    var data = storage.ReadFileBytes(file.FullName);
                    var blockData = new BlockData<BlockType>(data);
                    var block = blockData.Block;

                    if (block.BlockId == blockId && block.Issuer == issuer && block.Revision == revision)
                    {
                        if (!result.TryGetValue(block.BlockId, out var list))
                        {
                            list = new List<BlockData<BlockType>>();
                            result.Add(block.BlockId, list);
                        }

                        list.Add(blockData);
                    }
                }
                catch (Exception ex)
                {
                    Log.IgnoreException(ex);
                }
            }

            return result;
        }

        protected void NewVoteProcess(BlockType block, int revision)
        {
            if (block != null)
            {
                if (block.ChainType != ChainType)
                    return;

                if (block.ChainId != ChainId)
                    return;
            }

            var members = _coreChain.GetVoteMembers(ChainType, ChainId, ChainIndex, Time.Timestamp);
            lock (_lock)
            {
                _voteProcess?.Cancel();
                _voteProcess = null;
                if (members.Count > 0)
                {
                    _voteProcess = new VoteProcess<BlockType>(this, members, block, LocalKeyIndex, _localKey, revision);
                    _voteProcess.Start();
                }
            }
        }

        public abstract BlockType GetBlockProposal(VoteProcess<BlockType> voteProcess, BlockType lastBlock, int revision);
        public abstract bool CheckBlockProposal(VoteProcess<BlockType> voteProcess, BlockType block, BlockType lastBlock, out HashSet<long> invalidTransactionIds);

        public override Task OnMessage(CouncilMessage message, NodeConnection connection)
        {
            switch (message.MessageType)
            {
                case CouncilMessageTypes.BlockProposal:
                    return OnBlockProposal(message as CouncilBlockProposalMessage, connection);
                case CouncilMessageTypes.BlockVote:
                    return OnBlockVote(message as CouncilBlockVoteMessage, connection);
                case CouncilMessageTypes.BlockSignature:
                    return OnBlockSignature(message as CouncilBlockSignatureMessage, connection);
                case CouncilMessageTypes.CurrentRevision:
                    return OnCurrentRevision(message as CouncilCurrentRevisionMessage, connection);
            }

            return Task.CompletedTask;
        }

        Task OnBlockProposal(CouncilBlockProposalMessage message, NodeConnection connection)
        {
            VoteProcess<BlockType> voteProcess;
            lock (_lock)
                voteProcess = _voteProcess;

            if (voteProcess != null)
            {
                voteProcess.AddProposal(message.BlockId, message.Revision, message.Block as BlockType, message.Issuer);
            }

            return Task.CompletedTask;
        }

        Task OnBlockVote(CouncilBlockVoteMessage message, NodeConnection connection)
        {
            VoteProcess<BlockType> voteProcess;
            lock (_lock)
                voteProcess = _voteProcess;

            if (voteProcess != null)
            {
                voteProcess.AddVote(message.Vote, message.Issuer);
            }

            return Task.CompletedTask;
        }

        Task OnBlockSignature(CouncilBlockSignatureMessage message, NodeConnection connection)
        {
            VoteProcess<BlockType> voteProcess;
            lock (_lock)
                voteProcess = _voteProcess;

            if (voteProcess != null)
            {
                voteProcess.AddSignature(message.BlockSignatures, message.ProposalSignatures, message.Issuer);
            }

            return Task.CompletedTask;
        }

        Task OnCurrentRevision(CouncilCurrentRevisionMessage message, NodeConnection connection)
        {
            VoteProcess<BlockType> voteProcess;
            lock (_lock)
                voteProcess = _voteProcess;

            if (voteProcess != null)
            {
                // restart vote process
                if (message.BlockId == voteProcess.NextBlockId)
                {
                    if (message.Revision > voteProcess.CurrentRevision)
                        NewVoteProcess(voteProcess.LastBlock, message.Revision);
                    return Task.CompletedTask;
                }

                voteProcess.AddCurrentRevision(message.BlockId, message.Revision, message.Issuer);
            }

            return Task.CompletedTask;
        }

        public void BroadcastProposal(VoteProcess<BlockType> voteProcess, long blockId, int revision, BlockType block)
        {
            var proposalMessage = new CouncilBlockProposalMessage(blockId, revision, block, ChainType, ChainId, ChainIndex, voteProcess.LocalIssuer, voteProcess.LocalIssuerKey) { SignKey = _node.NodeConfiguration.LocaleNodePrivateKey };
            _ = Broadcast(proposalMessage, null);
        }

        public void BroadcastVote(VoteProcess<BlockType> voteProcess, Vote vote)
        {
            var voteMessage = new CouncilBlockVoteMessage(vote, ChainType, ChainId, ChainIndex, voteProcess.LocalIssuer, voteProcess.LocalIssuerKey) { SignKey = _node.NodeConfiguration.LocaleNodePrivateKey };
            _ = Broadcast(voteMessage, null);
        }

        public void BroadcastSignature(VoteProcess<BlockType> voteProcess, BlockSignatures blockSignatures, BlockProposalSignatures blockProposalSignatures)
        {
            var sigMessage = new CouncilBlockSignatureMessage(blockSignatures, blockProposalSignatures, ChainType, ChainId, ChainIndex, voteProcess.LocalIssuer, voteProcess.LocalIssuerKey) { SignKey = _node.NodeConfiguration.LocaleNodePrivateKey };
            _ = Broadcast(sigMessage, null);
        }

        public virtual void NewBlockAvailable(VoteProcess<BlockType> voteProcess, BlockData<BlockType> blockData, BlockProposalSignatures blockProposalSignatures)
        {
            try
            {
                var block = blockData.Block;

                if (_storage.WriteFileBytes(Path.Combine(_blockProposalPath, GetBlockProposalFileName(block)), blockData.ToByteArray()))
                {
                    lock (_lock)
                    {
                        if (!_proposals.TryGetValue(block.BlockId, out var list))
                        {
                            list = new List<BlockData<BlockType>>();
                            _proposals.Add(block.BlockId, list);
                        }

                        list.Add(blockData);
                    }
                }

                RemoveOldBlockProposals(block);
            }
            catch (Exception ex)
            {
                Log.IgnoreException(ex, this);
            }
        }

        public void BroadcastCurrentRevision(VoteProcess<BlockType> voteProcess, long blockId, int revision)
        {
            var revisionMessage = new CouncilCurrentRevisionMessage(blockId, revision, ChainType, ChainId, ChainIndex, voteProcess.LocalIssuer, voteProcess.LocalIssuerKey) { SignKey = _node.NodeConfiguration.LocaleNodePrivateKey };
            _ = Broadcast(revisionMessage, null);
        }
    }
}
