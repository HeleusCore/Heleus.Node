using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Chain.Blocks;
using Heleus.Cryptography;

namespace Heleus.ProofOfCouncil
{
    public class VoteProcess<BlockType> : ILogger where BlockType : Block
    {
        public readonly BlockType LastBlock;

        public readonly long NextBlockId;

        public string LogName
        {
            get
            {
                return $"VoteProcess<{typeof(BlockType).Name}>({_members.ChainId},{_members.ChainType})";
            }
        }

        public readonly short LocalIssuer;
        public readonly Key LocalIssuerKey;

        int _localRevision;

        public int CurrentRevision
        {
            get
            {
                lock (_lock)
                    return _localRevision;
            }
        }

        readonly object _lock = new object();

        readonly CancellationTokenSource _cancelToken = new CancellationTokenSource();

        readonly Dictionary<short, SortedList<int, VoteProposal>> _voteKeyProposals = new Dictionary<short, SortedList<int, VoteProposal>>();
        readonly SortedList<int, HashSet<short>> _revisionsVoteKeys = new SortedList<int, HashSet<short>>();
        readonly VoteMembers _members;

        readonly List<Vote> _newVotes = new List<Vote>();

        readonly IVoteProcessHandler<BlockType> _voteHandler;

        public VoteProcess(IVoteProcessHandler<BlockType> voteHandler, VoteMembers voteMembers, BlockType lastBlock, short issuer, Key issuerKey, int revision)
        {
            _voteHandler = voteHandler;

            LastBlock = lastBlock;
            if (LastBlock != null)
                NextBlockId = LastBlock.BlockId + 1;
            else
                NextBlockId = Protocol.GenesisBlockId;

            LocalIssuer = issuer;
            LocalIssuerKey = issuerKey;

            _members = voteMembers;
            _localRevision = revision;

            foreach (var key in _members.VoteKeys.Values)
                _voteKeyProposals.Add(key.KeyIndex, new SortedList<int, VoteProposal>());
        }

        bool HasEnoughLegitProposals()
        {
            var valids = 0;
            lock (_lock)
            {
                foreach (var proposals in _voteKeyProposals.Values)
                {
                    if (proposals.TryGetValue(_localRevision, out var proposal))
                    {
                        if (proposal.ValidProposal)
                            valids++;
                    }
                }
            }

            var requiredProposals = 1;
            if (_members.Count > 1)
                requiredProposals = 2;

            requiredProposals = Math.Max((_members.Count / 4) + 1, requiredProposals);

            return valids >= requiredProposals;
        }

        VoteProposal GetBestProposal()
        {
            VoteProposal bestProposal = null;
            lock (_lock)
            {
                foreach (var proposals in _voteKeyProposals.Values)
                {
                    if (proposals.TryGetValue(_localRevision, out var proposal))
                    {
                        if (proposal.ValidProposal)
                        {
                            var block = proposal.Block;
                            if (bestProposal == null)
                            {
                                bestProposal = proposal;
                            }
                            else
                            {
                                if (block.TransactionCount > bestProposal.Block.TransactionCount)
                                {
                                    bestProposal = proposal;
                                }
                                else if (block.TransactionCount == bestProposal.Block.TransactionCount)
                                {
                                    if (proposal.VoterDistance > bestProposal.VoterDistance)
                                    {
                                        bestProposal = proposal;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return bestProposal;
        }

        async Task PhaseTimerLoop()
        {
            while (true)
            {
                try
                {
                    if (_cancelToken.IsCancellationRequested)
                        return;

                    _voteHandler.BroadcastCurrentRevision(this, NextBlockId, _localRevision);
                    AddCurrentRevision(NextBlockId, _localRevision, LocalIssuer);

                    await Task.Delay(750, _cancelToken.Token);
                    if (_cancelToken.IsCancellationRequested)
                        return;

                    if (!HasEnoughLegitProposals())
                    {
                        var localBlock = _voteHandler.GetBlockProposal(this, LastBlock, _localRevision);
                        if (localBlock != null)
                        {
                            var vote = AddProposal(NextBlockId, _localRevision, localBlock, LocalIssuer);
                            _voteHandler.BroadcastProposal(this, NextBlockId, _localRevision, localBlock);
                        }
                    }

                    if (_cancelToken.IsCancellationRequested)
                        return;

                    await Task.Delay(750, _cancelToken.Token);
                    if (_cancelToken.IsCancellationRequested)
                        return;

                    var proposal = GetBestProposal();
                    if (proposal == null)
                    {
                        goto repeat;
                    }

                    var block = proposal.Block;
                    var blockSignatures = new BlockSignatures(block);
                    blockSignatures.AddSignature(LocalIssuer, block, LocalIssuerKey);

                    var blockProposalSignatures = new BlockProposalSignatures(block);
                    blockProposalSignatures.AddSignature(LocalIssuer, block, LocalIssuerKey);

                    _voteHandler.BroadcastSignature(this, blockSignatures, blockProposalSignatures);
                    AddSignature(blockSignatures, blockProposalSignatures, LocalIssuer);

                    if (_cancelToken.IsCancellationRequested)
                        return;

                    await Task.Delay(750, _cancelToken.Token);
                    if (_cancelToken.IsCancellationRequested)
                        return;

                    repeat:
                    Log.Trace($"No approved block found for blockid {NextBlockId} with revision {_localRevision}.", this);
                    // get the highest revision with a certain amount of approval

                    var approvedRevision = 0;
                    lock (_lock)
                    {
                        var revisions = _revisionsVoteKeys.Reverse();
                        foreach (var r in revisions)
                        {
                            if ((r.Value.Count / (float)_members.Count + float.Epsilon) >= 0.5f)
                            {
                                approvedRevision = r.Key;
                                break;
                            }
                        }
                    }

                    _localRevision++;
                    if (approvedRevision > _localRevision)
                        Log.Trace($"Resetting revision from {_localRevision} to {approvedRevision} for blockid {NextBlockId}.", this);

                    _localRevision = Math.Max(_localRevision, approvedRevision);
                }
                catch (TaskCanceledException) { }
                catch (Exception ex)
                {
                    Log.HandleException(ex, this);
                }
            }
        }

        public Vote AddProposal(long blockId, int revision, BlockType block, short issuer)
        {
            Log.Trace($"AddProposal blockid {blockId} revision {revision} proposal issuer {issuer} block issuer {block?.Issuer}.", this);

            (var result, var invalids) = Add(blockId, revision, block, issuer);
            var vote = new Vote(result, block, blockId, revision, issuer, LocalIssuer, invalids);
            AddVote(vote, vote.VoteIssuer);

            _voteHandler.BroadcastVote(this, vote);
            return vote;
        }

        public void AddVote(Vote vote, short voteIssuer)
        {
            Log.Trace($"AddVote {vote.Result} revision: {vote.BlockRevision} block issuer: {vote.BlockIssuer} vote issuer: {voteIssuer}.", this);

            if (vote.VoteIssuer != voteIssuer)
                return;

            if (vote.BlockId != NextBlockId)
                return;

            lock (_lock)
            {
                _newVotes.Add(vote);
            }

            UpdateVotes();
        }

        public void AddSignature(BlockSignatures signatures, BlockProposalSignatures proposalSignatures, short issuer)
        {
            BlockData<BlockType> resultBlockData = null;
            BlockProposalSignatures resultProposalSignatures = null;

            Log.Trace($"Signature revision: {signatures.Revision} issuer: {issuer}.", this);
            lock (_lock)
            {
                _voteKeyProposals[signatures.BlockIssuer].TryGetValue(signatures.Revision, out var proposal);
                if (proposal != null)
                {
                    proposal.AddSignature(signatures, proposalSignatures, issuer);
                    if (proposal.IsBlockSignatureValid && proposal.IsBlockProposalSignatureValid)
                    {
                        resultBlockData = new BlockData<BlockType>(proposal.Block as BlockType, proposal.Signatures);
                        resultProposalSignatures = proposal.ProposalSignatures;
                    }
                }
            }

            if (resultBlockData != null && resultProposalSignatures != null)
            {
                _voteHandler.NewBlockAvailable(this, resultBlockData, resultProposalSignatures);
            }
        }

        public void AddCurrentRevision(long blockId, int revision, short issuer)
        {
            if (blockId != NextBlockId)
                return;

            lock (_lock)
            {
                if (!_revisionsVoteKeys.ContainsKey(revision))
                    _revisionsVoteKeys[revision] = new HashSet<short>();

                _revisionsVoteKeys[revision].Add(issuer);
            }
        }

        (VoteResultTypes, HashSet<long>) Add(long blockId, int revision, BlockType block, short issuerKeyIndex)
        {
            if (blockId != NextBlockId)
                return (VoteResultTypes.InvalidBlockId, null);

            if (!_members.ContainsKey(issuerKeyIndex))
                return (VoteResultTypes.InvalidIssuer, null);

            if (block != null)
            {
                if (block.BlockId != blockId || block.Revision != revision)
                    return (VoteResultTypes.InvalidBlockId, null);
            }

            if (Math.Abs(_localRevision - revision) > 1)
                //if (_localRevision != revision)
                return (VoteResultTypes.InvalidRevision, null);

            var proposals = _voteKeyProposals[issuerKeyIndex];

            var prop = new VoteProposal(_members, blockId, revision, block, issuerKeyIndex);
            lock (_lock)
            {
                proposals[revision] = prop;
            }

            UpdateVotes();

            if (block == null)
                return (VoteResultTypes.EmptyProposal, null);

            var valid = _voteHandler.CheckBlockProposal(this, block, LastBlock, out var invalidTransactionIds);

            if (!valid)
                return (VoteResultTypes.InvalidTransactions, invalidTransactionIds);

            return (VoteResultTypes.Ok, null);
        }

        void UpdateVotes()
        {
            lock (_lock)
            {
                for (var i = _newVotes.Count - 1; i >= 0; i--)
                {
                    var vote = _newVotes[i];

                    try
                    {
                        if (_members.ContainsKey(vote.BlockIssuer))
                        {
                            var proposalList = _voteKeyProposals[vote.BlockIssuer];
                            if (proposalList.TryGetValue(vote.BlockRevision, out var proposal))
                            {
                                if (proposal.Block != null)
                                {
                                    if (vote.BlockHash != proposal.Block.BlockHash)
                                        continue;
                                }

                                proposal.AddVote(vote);
                                _newVotes.RemoveAt(i);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.IgnoreException(ex, this);
                    }
                }
            }
        }

        public void Start()
        {
            if (_cancelToken.IsCancellationRequested)
                return;

            TaskRunner.Run(() => PhaseTimerLoop());
        }

        public void Cancel()
        {
            if (_cancelToken.IsCancellationRequested)
                return;

            _cancelToken.Cancel();
        }
    }
}
