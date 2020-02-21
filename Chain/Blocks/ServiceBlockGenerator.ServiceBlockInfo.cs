using System.Collections.Generic;
using Heleus.Chain.Service;
using Heleus.Transactions;
using Heleus.Transactions.Features;

namespace Heleus.Chain.Blocks
{
    public partial class ServiceBlockGenerator
    {
        class ServiceBlockInfo
        {
            public readonly BlockTransactionGenerator FeatureGenerator;

            public readonly SortedList<long, JoinServiceTransaction> Joins = new SortedList<long, JoinServiceTransaction>();
            public readonly SortedList<long, PurchaseServiceTransaction> Purchases = new SortedList<long, PurchaseServiceTransaction>();

            public readonly SortedList<long, RequestRevenueServiceTransaction> Revenues = new SortedList<long, RequestRevenueServiceTransaction>();

            public readonly List<ServiceTransaction> Transactions = new List<ServiceTransaction>();

            public bool HasTransaction => Transactions.Count > 0;

            public ServiceBlockInfo(ServiceChain serviceChain)
            {
                FeatureGenerator = new BlockTransactionGenerator(serviceChain);
            }

            public bool AddJoin(JoinServiceTransaction joinTransaction, ServiceAccount serviceAccount, ServiceChain serviceChain)
            {
                var accountId = joinTransaction.AccountId;
                if (!Joins.ContainsKey(accountId))
                {
                    Joins[accountId] = joinTransaction;
                    Transactions.Add(joinTransaction);
                    FeatureGenerator.AddTransaction(BlockTransactionGeneratorMode.Preprocess, serviceChain, joinTransaction, serviceAccount);

                    return true;
                }
                return false;
            }

            public bool AddPurchase(PurchaseServiceTransaction purchaseTransaction, ServiceAccount serviceAccount, ServiceChain serviceChain)
            {
                var accountId = purchaseTransaction.AccountId;
                if (!Purchases.ContainsKey(accountId))
                {
                    Purchases[accountId] = purchaseTransaction;
                    Transactions.Add(purchaseTransaction);
                    FeatureGenerator.AddTransaction(BlockTransactionGeneratorMode.Preprocess, serviceChain, purchaseTransaction, serviceAccount);

                    return true;
                }
                return false;
            }

            public bool AddRevenue(RequestRevenueServiceTransaction revenueTransaction, ServiceAccount serviceAccount, ServiceChain serviceChain)
            {
                var accountId = revenueTransaction.AccountId;
                if(!Revenues.ContainsKey(accountId))
                {
                    Revenues[accountId] = revenueTransaction;
                    Transactions.Add(revenueTransaction);
                    FeatureGenerator.AddTransaction(BlockTransactionGeneratorMode.Preprocess, serviceChain, revenueTransaction, serviceAccount);

                    return true;
                }

                return false;
            }

            public void Sort()
            {
                Transactions.Sort((a, b) => a.UniqueIdentifier.CompareTo(b.UniqueIdentifier));
            }
        }
    }
}
