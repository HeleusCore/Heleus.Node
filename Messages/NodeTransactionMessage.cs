using System;
using Heleus.Base;
using Heleus.Chain;
using Heleus.Chain.Service;
using Heleus.Operations;
using Heleus.Transactions;

namespace Heleus.Messages
{
    public class NodeTransactionMessagePayload : IPackable
    {
        public readonly TransactionValidation Validation;

        public NodeTransactionMessagePayload(TransactionValidation validation)
        {
            Validation = validation;
        }

        public NodeTransactionMessagePayload(Unpacker unpacker)
        {
            if (unpacker.UnpackBool())
                Validation = new TransactionValidation(unpacker);
        }

        public void Pack(Packer packer)
        {
            if (packer.Pack(Validation != null))
                Validation.Pack(packer);
        }
    }

    public class NodeTransactionMessage : NodeMessage
    {
		public Transaction Transaction { get; private set; }
        public int Hops { get; private set; }
		public NodeTransactionMessagePayload Payload { get; private set; }

        public NodeTransactionMessage() : base(NodeMessageTypes.Transaction)
        {
        }

        public NodeTransactionMessage(Transaction transaction, int hops = 0) : this()
		{
			Transaction = transaction;
			Hops = hops;
		}

        public NodeTransactionMessage(Transaction transaction, NodeTransactionMessagePayload payload, int hops = 0) : this()
        {
            Transaction = transaction;
            Hops = hops;
            Payload = payload;
        }

        protected override void Pack(Packer packer)
		{
			base.Pack(packer);

			Transaction.Store(packer);
			packer.Pack(Hops);
            if (packer.Pack(Payload != null))
                Payload.Pack(packer);

		}

		protected override void Unpack(Unpacker unpacker)
		{
			base.Unpack(unpacker);
			Transaction = Operation.Restore<Transaction>(unpacker);
            Hops = unpacker.UnpackInt();
            if (unpacker.UnpackBool())
                Payload = new NodeTransactionMessagePayload(unpacker);
		}
	}
}
