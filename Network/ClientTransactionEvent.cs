using System;
using Heleus.Chain;
using Heleus.Transactions;

namespace Heleus.Network
{
    public class ClientTransactionEvent
    {
        public readonly Transaction Transation;

        public ClientTransactionEvent(Transaction transaction)
        {
            Transation = transaction;
        }
    }
}
