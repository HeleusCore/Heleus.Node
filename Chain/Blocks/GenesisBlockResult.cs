using System.Collections.Generic;
using Heleus.Cryptography;
using Heleus.Transactions;

namespace Heleus.Chain.Blocks
{
    public class GenesisBlockResult
    {
        public readonly CoreBlock Block;
        public readonly BlockSignatures Signature;

        public readonly Key NetworkPublicKey;

        public readonly ChainKeyStore NetworkVoteKey;
        public readonly string NetworkVotePassword;

        public readonly IReadOnlyDictionary<int, List<ServiceTransaction>> ServiceTransactions;

        public GenesisBlockResult(CoreBlock block, BlockSignatures signature, Key networkPublicKey, ChainKeyStore voteKey, string votePassword, Dictionary<int, List<ServiceTransaction>> serviceTransactions)
        {
            Block = block;
            Signature = signature;
            NetworkPublicKey = networkPublicKey;
            NetworkVoteKey = voteKey;
            NetworkVotePassword = votePassword;
            ServiceTransactions = serviceTransactions;
        }
    }
}
