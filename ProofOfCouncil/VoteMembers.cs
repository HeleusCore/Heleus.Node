using System.Collections.Generic;
using Heleus.Base;
using Heleus.Chain;
using Heleus.Chain.Blocks;
using Heleus.Cryptography;

namespace Heleus.ProofOfCouncil
{
    public class VoteMembers
    {
        public readonly int ChainId;
        public readonly ChainType ChainType;

        public int Count => _voteKeys.Count;

        public IReadOnlyDictionary<short, PublicChainKey> VoteKeys => _voteKeys;

        readonly Dictionary<short, PublicChainKey> _voteKeys;

        public PublicChainKey GetKey(short issuer)
        {
            _voteKeys.TryGetValue(issuer, out var key);
            return key;
        }

        public bool ContainsKey(short issuer) => _voteKeys.ContainsKey(issuer);

        public VoteMembers(Dictionary<short, PublicChainKey> voteKeys, int chainId, ChainType blockType)
        {
            _voteKeys = voteKeys;
            ChainId = chainId;
            ChainType = blockType;
        }

        public bool IsBlockSignatureValid(Block block, BlockSignaturesBase signatures)
        {
            var issuers = signatures?.GetIssuers();
            var validCount = 0;

            if (issuers == null || block == null)
                return false;

            foreach (var issuer in issuers)
            {
                if (_voteKeys.TryGetValue(issuer, out var key))
                {
                    if (signatures.IsSignatureValid(key.PublicKey, issuer, block))
                        validCount++;
                }
            }

            return IsVotingValid(validCount);
        }

        public bool IsBlockSignatureValid(BlockData blockData)
        {
            var issuers = blockData?.Signatures?.GetIssuers();
            var validCount = 0;

            if (issuers == null)
                return false;

            var signatures = blockData.Signatures;
            foreach (var issuer in issuers)
            {
                if (_voteKeys.TryGetValue(issuer, out var key))
                {
                    if (signatures.IsSignatureValid(key.PublicKey, issuer, blockData.Block))
                        validCount++;
                }
            }

            return IsVotingValid(validCount);
        }

        public bool IsVotingValid(int count)
        {
            return Mth.Percentage(count, _voteKeys.Count) > 50f;
        }
    }
}
