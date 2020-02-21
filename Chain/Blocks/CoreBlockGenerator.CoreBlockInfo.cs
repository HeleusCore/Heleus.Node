using System.Collections.Generic;
using Heleus.Chain.Core;
using Heleus.Operations;
using Heleus.Transactions;

namespace Heleus.Chain.Blocks
{
    public partial class CoreBlockGenerator : BlockGenerator
    {
        class CoreBlockInfo
        {
            public class AccountRegistrationInfo
            {
                public readonly AccountRegistrationCoreTransaction Transaction;
                public readonly AccountOperation Operation;

                public AccountRegistrationInfo(AccountRegistrationCoreTransaction transaction, AccountOperation operation)
                {
                    Transaction = transaction;
                    Operation = operation;
                }
            }

            public class ChainUpdateInfo
            {
                public readonly ChainRegistrationCoreTransaction Transaction;
                public readonly ChainInfoOperation Operation;

                public ChainUpdateInfo(ChainRegistrationCoreTransaction transaction, ChainInfoOperation operation)
                {
                    Transaction = transaction;
                    Operation = operation;
                }
            }

            public class UpdateInfo
            {
                public bool HasUpdates => Transactions.Count > 0 || Purchases.Count > 0 || Joins.Count > 0 || Revenues.Count > 0;

                public readonly HashSet<long> AffectedAccounts = new HashSet<long>();
                public readonly List<TransferCoreTransaction> Transactions = new List<TransferCoreTransaction>();

                public readonly List<PurchaseServiceTransaction> Purchases = new List<PurchaseServiceTransaction>();
                public readonly List<JoinServiceTransaction> Joins = new List<JoinServiceTransaction>();
                public readonly List<RequestRevenueServiceTransaction> Revenues = new List<RequestRevenueServiceTransaction>();

                public readonly AccountUpdateOperation Operation = new AccountUpdateOperation();
            }

            public readonly List<AccountRegistrationInfo> NewAccounts = new List<AccountRegistrationInfo>();
            public readonly List<ChainUpdateInfo> Chains = new List<ChainUpdateInfo>();

            public readonly SortedList<int, ServiceBlockCoreTransaction> ServiceBlocks = new SortedList<int, ServiceBlockCoreTransaction>();

            public readonly UpdateInfo AccountUpdates = new UpdateInfo();

            public void AddNewAccount(AccountRegistrationCoreTransaction transaction, CoreAccount account)
            {
                NewAccounts.Add(new AccountRegistrationInfo(transaction, new AccountOperation(account.AccountId, account.AccountKey, transaction.Timestamp)));
            }

            public void AddChainUpdate(ChainRegistrationCoreTransaction transaction, ChainInfoOperation operation)
            {
                Chains.Add(new ChainUpdateInfo(transaction, operation));
            }

            public void AddTransfer(TransferCoreTransaction transaction)
            {
                AccountUpdates.Transactions.Add(transaction);

                AccountUpdates.AffectedAccounts.Add(transaction.AccountId);
                AccountUpdates.AffectedAccounts.Add(transaction.ReceiverAccountId);
            }

            public void AddServiceBlock(ServiceBlockCoreTransaction transaction)
            {
                var chainId = transaction.ServiceBlock.ChainId;
                if (ServiceBlocks.TryGetValue(chainId, out var currentTransaction))
                {
                    if (transaction.Timestamp > currentTransaction.Timestamp)
                        ServiceBlocks[chainId] = transaction;
                }
                else
                {
                    ServiceBlocks.Add(chainId, transaction);
                }
            }

            public bool HasTransaction
            {
                get
                {
                    return AccountUpdates.HasUpdates || NewAccounts.Count > 0 || Chains.Count > 0;
                }
            }

            public void Sort()
            {
                NewAccounts.Sort((a, b) => a.Operation.AccountId.CompareTo(b.Operation.AccountId));
                Chains.Sort((a, b) => a.Operation.ChainId.CompareTo(b.Operation.ChainId));
                AccountUpdates.Transactions.Sort((a, b) => a.UniqueIdentifier.CompareTo(b.UniqueIdentifier));
                AccountUpdates.Joins.Sort((a, b) => a.UniqueIdentifier.CompareTo(b.UniqueIdentifier));
                AccountUpdates.Purchases.Sort((a, b) => a.UniqueIdentifier.CompareTo(b.UniqueIdentifier));
                AccountUpdates.Revenues.Sort((a, b) => a.UniqueIdentifier.CompareTo(b.UniqueIdentifier));
            }
        }
    }
}
