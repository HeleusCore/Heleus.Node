using Heleus.Base;
using Heleus.Cryptography;
using Heleus.Transactions;

namespace Heleus.Chain
{
    public class TransactionValidation : IPackable
    {
        public readonly long TransactionId;
        public readonly short KeyIndex;
        public readonly Signature Signature;

        public TransactionValidation(Transaction transaction, short keyIndex, Key signKey)
        {
            TransactionId = transaction.TransactionId;
            KeyIndex = keyIndex;
            Signature = Signature.Generate(signKey, transaction.SignatureHash);
        }

        public TransactionValidation(Unpacker unpacker)
        {
            unpacker.Unpack(out TransactionId);
            unpacker.Unpack(out KeyIndex);
            unpacker.Unpack(out Signature);
        }

        public void Pack(Packer packer)
        {
            packer.Pack(TransactionId);
            packer.Pack(KeyIndex);
            packer.Pack(Signature);
        }

        public bool IsValid(Transaction transaction, Key publicKey)
        {
            if (transaction == null || transaction.TransactionId != TransactionId || publicKey == null || Signature == null)
                return false;

            return Signature.IsValid(publicKey, transaction.SignatureHash);
        }
    }
}
