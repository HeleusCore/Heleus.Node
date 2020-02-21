using System;
using System.Collections.Generic;
using Heleus.Base;
using Heleus.Chain.Blocks;

namespace Heleus.ProofOfCouncil
{
    public class VoteProposal : ILogger
    {
        public readonly long BlockId;
        public readonly int Revision;

        public readonly Block Block;
        public BlockSignatures Signatures { get; private set; }
        public BlockProposalSignatures ProposalSignatures { get; private set; }

        readonly short _issuerKeyIndex;
        public readonly VoteMembers _members;
        int _positiveVotes;
        readonly Dictionary<short, Vote> _votes = new Dictionary<short, Vote>();

        public string LogName => nameof(VoteProposal);

        public bool ValidProposal
        {
            get
            {
                lock (_votes)
                {
                    return Block != null && _members.IsVotingValid(_positiveVotes);
                }
            }
        }

        public int VoterDistance
        {
            get
            {
                var index = 0;
                foreach(var key in _members.VoteKeys.Values)
                {
                    if (key.KeyIndex == _issuerKeyIndex)
                        break;
                    index++;
                }

                var target = (BlockId % _members.Count);
                var distance = 0;

                while (true)
                {
                    if (index == target)
                    {
                        return distance;
                    }

                    distance++;
                    index++;
                    index %= _members.Count;
                }

                // target = 4
                // index = 7
                // members = 10
                // d=0, i=7; d=1, i=8; d=2, i=9; d=3, i=0; d=4, i=1; d=5, i=2; d=6, i=3; d=7, i=4 == target
            }
        }

        public VoteProposal(VoteMembers members, long blockId, int revision, Block blockData, short issuerKeyIndex)
        {
            BlockId = blockId;
            Revision = revision;
            Block = blockData;
            _issuerKeyIndex = issuerKeyIndex;

            _members = members;
            foreach (var key in _members.VoteKeys.Values)
                _votes.Add(key.KeyIndex, null);
        }

        public void AddVote(Vote vote)
        {
            lock (_votes)
            {
                var ps = 0;

                var issuer = vote.VoteIssuer;
                _votes.TryGetValue(issuer, out var oldVote);
                if (oldVote != null && vote.Timestamp < oldVote.Timestamp)
                    return;

                _votes[issuer] = vote;

                foreach (var v in _votes.Values)
                {
                    if (v != null)
                    {
                        if (vote.Result == VoteResultTypes.Ok)
                            ps++;
                    }
                }

                _positiveVotes = ps;
            }
        }

        public void AddSignature(BlockSignatures signatures, BlockProposalSignatures proposalSignatures, short issuer)
        {
            var block = Block;
            if (block != null)
            {
                var memeberKey = _members.GetKey(issuer);
                if (signatures.IsSignatureValid(memeberKey?.PublicKey, issuer, block) &&
                    proposalSignatures.IsSignatureValid(memeberKey?.PublicKey, issuer, block))
                {
                    lock (_votes)
                    {
                        if (Signatures == null)
                            Signatures = new BlockSignatures(block);

                        if (ProposalSignatures == null)
                            ProposalSignatures = new BlockProposalSignatures(block);

                        Signatures.AddSignature(signatures.GetSignature(issuer));
                        ProposalSignatures.AddSignature(proposalSignatures.GetSignature(issuer));
                    }
                }
            }
        }

        public bool IsBlockSignatureValid
        {
            get
            {
                lock (_votes)
                {
                    if (Signatures == null)
                        return false;
                    if (Block == null)
                        return false;

                    return _members.IsBlockSignatureValid(Block, Signatures);
                }
            }
        }

        public bool IsBlockProposalSignatureValid
        {
            get
            {
                lock (_votes)
                {
                    if (ProposalSignatures == null)
                        return false;
                    if (Block == null)
                        return false;

                    return _members.IsBlockSignatureValid(Block, Signatures);
                }
            }
        }
    }
}
