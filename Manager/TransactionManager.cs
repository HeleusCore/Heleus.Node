using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Chain;
using Heleus.Chain.Blocks;
using Heleus.Messages;
using Heleus.Network;
using Heleus.Node.Configuration;
using Heleus.Transactions;

namespace Heleus.Manager
{
    public class TransactionManager : ILogger
    {
        enum SenderTypes
        {
            Client,
            Node
        }

        class Item
        {
            public readonly long Payload; // hops or request code
            public readonly SenderTypes Sender;
            public readonly Connection Connection;

            public Item(long payload, SenderTypes sender, Connection connection)
            {
                Payload = payload;
                Sender = sender;
                Connection = connection;
            }
        }

        class CoreItem : Item
        {
            public readonly CoreTransaction Transaction;

            public CoreItem(long payload, SenderTypes sender, CoreTransaction transaction, Connection connection) : base(payload, sender, connection)
            {
                Transaction = transaction;
            }
        }

        class ServiceItem : Item
        {
            public readonly ServiceTransaction Transaction;

            public ServiceItem(long payload, SenderTypes sender, ServiceTransaction transaction, Connection connection) : base(payload, sender, connection)
            {
                Transaction = transaction;
            }
        }

        class DataItem : Item
        {
            public readonly DataTransaction Transaction;
            public readonly TransactionValidation NodeValidation;

            public DataItem(long payload, SenderTypes sender, DataTransaction transaction, TransactionValidation nodeValidation, Connection connection) : base(payload, sender, connection)
            {
                Transaction = transaction;
                NodeValidation = nodeValidation;
            }
        }

        class MaintainItem : Item
        {
            public readonly MaintainTransaction Transaction;

            public MaintainItem(long payload, MaintainTransaction maintainTransaction, Connection connection) : base(payload, SenderTypes.Node, connection)
            {
                Transaction = maintainTransaction;
            }
        }

        List<CoreItem> _coreTransactions = new List<CoreItem>();
        List<ServiceItem> _serviceTransactions = new List<ServiceItem>();
        List<DataItem> _dataTransactions = new List<DataItem>();
        List<MaintainItem> _maintainTransactions = new List<MaintainItem>();

        readonly object _lock = new object();

        readonly LazyLookupTable<long, Transaction> _transactionsLookup = new LazyLookupTable<long, Transaction> { LifeSpan = TimeSpan.FromSeconds(Protocol.TransactionTTL), Depth = 5 };

        readonly Node.Node _node;
        readonly ChainManager _chainManager;
        readonly NodeConfiguration _configuration;

        public string LogName => GetType().Name;

        public TransactionManager(Node.Node node)
        {
            _node = node;
            _chainManager = node.ChainManager;
            _configuration = node.NodeConfiguration;
        }

        public void Start()
        {
            TaskRunner.Run(() => Loop());
        }

        public void Stop()
        {
        }

        async Task Loop()
        {
            while (!_node.HasQuit)
            {
                try
                {
                    List<CoreItem> coreTransactions = null;
                    List<ServiceItem> serviceTransactions = null;
                    List<DataItem> dataTransactions = null;
                    List<MaintainItem> maintainTransactions = null;

                    lock (_lock)
                    {
                        if (_coreTransactions.Count > 0)
                        {
                            coreTransactions = _coreTransactions;
                            _coreTransactions = new List<CoreItem>();
                        }

                        if (_dataTransactions.Count > 0)
                        {
                            dataTransactions = _dataTransactions;
                            _dataTransactions = new List<DataItem>();
                        }

                        if (_serviceTransactions.Count > 0)
                        {
                            serviceTransactions = _serviceTransactions;
                            _serviceTransactions = new List<ServiceItem>();
                        }

                        if(_maintainTransactions.Count > 0)
                        {
                            maintainTransactions = _maintainTransactions;
                            _maintainTransactions = new List<MaintainItem>();
                        }
                    }

                    if (coreTransactions != null)
                    {
                        var chain = _chainManager.CoreChain;
                        var generator = new CoreBlockGenerator(chain, chain.BlockStorage.LastBlock as CoreBlock);

                        foreach (var transactionItem in coreTransactions)
                        {
                            var transaction = transactionItem.Transaction;
                            var sender = transactionItem.Sender;
                            var nodeConnection = transactionItem.Connection as NodeConnection;

                            var check = generator.ConsumeTransaction(transactionItem.Transaction);
                            if (check == TransactionResultTypes.Ok)
                            {
                                if (!_node.CouncilManager.HandleNewTransaction(transaction, null, nodeConnection))
                                    _ = _node.NodeServer.Broadcast(new NodeTransactionMessage(transaction, sender == SenderTypes.Client ? 0 : (int)transactionItem.Payload) { SignKey = _configuration.LocaleNodePrivateKey }, new HashSet<NodeConnection> { nodeConnection });
                            }
                            else
                            {
                                if (sender == SenderTypes.Client)
                                {
                                    var requestCode = transactionItem.Payload;
                                    if (requestCode != 0)
                                        await transactionItem.Connection.Send(new ClientTransactionResponseMessage(requestCode, check, null) { SignKey = _configuration.LocaleNodePrivateKey });
                                }
                            }
                        }
                    }

                    if (serviceTransactions != null)
                    {
                        var generators = new Dictionary<int, ServiceBlockGenerator>();
                        foreach (var transactionItem in serviceTransactions)
                        {
                            var transaction = transactionItem.Transaction;
                            var sender = transactionItem.Sender;
                            var chainid = transaction.TargetChainId;
                            var serviceChain = _chainManager.GetServiceChain(chainid);

                            var validationResult = await serviceChain.ValidateServiceTransaction(transaction);
                            var result = validationResult.Result;
                            var userCode = validationResult.UserCode;
                            var message = validationResult.Message;

                            if (result == TransactionResultTypes.Ok)
                            {
                                if (!generators.TryGetValue(chainid, out var generator))
                                {
                                    generator = new ServiceBlockGenerator(_chainManager.CoreChain, serviceChain, _chainManager.GetMaintainChain(chainid), serviceChain.BlockStorage.LastBlock as ServiceBlock);
                                    generators[chainid] = generator;
                                }

                                result = generator.ConsumeTransaction(transaction);
                            }

                            if (result == TransactionResultTypes.Ok)
                            {
                                _node.CouncilManager.HandleNewTransaction(transaction, null, transactionItem.Connection as NodeConnection);
                            }
                            else
                            {
                                if (sender == SenderTypes.Client)
                                {
                                    var requestCode = transactionItem.Payload;
                                    if (requestCode != 0)
                                        await transactionItem.Connection.Send(new ClientTransactionResponseMessage(requestCode, userCode, message, result, null) { SignKey = _configuration.LocaleNodePrivateKey });
                                }
                            }
                        }
                    }

                    if (dataTransactions != null)
                    {
                        foreach (var transactionItem in dataTransactions)
                        {
                            var transaction = transactionItem.Transaction;
                            var chainid = transaction.TargetChainId;
                            var sender = transactionItem.Sender;
                            var dataChain = _chainManager.GetDataChain(chainid, transaction.ChainIndex);

                            var validation = transactionItem.NodeValidation;
                            if(validation != null)
                            {
                                var chainKey = _node.ChainManager.CoreChain.GetChainInfo(chainid)?.GetValidChainKey(transaction.ChainIndex, validation.KeyIndex, transaction.Timestamp);
                                if(!validation.IsValid(transaction, chainKey?.PublicKey))
                                {
                                    validation = null;
                                }
                            }

                            if (validation != null)
                            {
                                _node.CouncilManager.HandleNewTransaction(transaction, validation, transactionItem.Connection as NodeConnection);
                            }
                            else
                            {
                                var validationResult = await dataChain.ValidateDataTransaction(transaction);
                                validation = validationResult.TransactionValidation;
                                var result = validationResult.Result;
                                var userCode = validationResult.UserCode;
                                var message = validationResult.Message;

                                if (result == TransactionResultTypes.Ok)
                                {
                                    _node.CouncilManager.HandleNewTransaction(transaction, validation, transactionItem.Connection as NodeConnection);
                                }
                                else
                                {
                                    if (sender == SenderTypes.Client)
                                    {
                                        var requestCode = transactionItem.Payload;
                                        if (requestCode != 0)
                                            await transactionItem.Connection.Send(new ClientTransactionResponseMessage(requestCode, userCode, message, result, null) { SignKey = _configuration.LocaleNodePrivateKey });
                                    }
                                }
                            }
                        }
                    }

                    if(maintainTransactions != null)
                    {
                        var generators = new Dictionary<int, MaintainBlockGenerator>();
                        foreach (var item in maintainTransactions)
                        {
                            var transaction = item.Transaction;
                            var chainId = transaction.TargetChainId;
                            var chain = _chainManager.GetMaintainChain(chainId);

                            var result = chain.ValidateMaintainTransaction(transaction);
                            if(result == TransactionResultTypes.Ok)
                            {
                                if (!_node.CouncilManager.HandleNewTransaction(transaction, null, null))
                                    _ = _node.NodeServer.Broadcast(new NodeTransactionMessage(transaction, (int)item.Payload) { SignKey = _configuration.LocaleNodePrivateKey }, null);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.HandleException(ex, this);
                }

                await Task.Delay(50);
            }
        }

        TransactionResultTypes PreCheck(Transaction transaction)
        {
            if (transaction == null)
                return TransactionResultTypes.InvalidTransaction;

            if (transaction.IsExpired(false))
                return TransactionResultTypes.Expired;

            var chain = _chainManager.GetChain(transaction.TargetChainType, transaction.TargetChainId, transaction.ChainIndex);
            if (chain == null)
                return TransactionResultTypes.ChainNodeInvalid;

            if (transaction.TargetChainType != ChainType.Core)
            {
                var service = _chainManager.GetService(transaction.TargetChainId);
                if (service == null)
                    return TransactionResultTypes.ChainServiceUnavailable;
            }

            if (_transactionsLookup.Contains(transaction.UniqueIdentifier))
                return TransactionResultTypes.AlreadySubmitted;
            _transactionsLookup[transaction.UniqueIdentifier] = null; //transaction; // store the transaction? Not needed imho

            var r = chain.BlockStorage.HistoryContainsTransactionOrRegistration(transaction);
            if (r != TransactionResultTypes.Ok)
                return r;

            return TransactionResultTypes.Ok;
        }

        public TransactionResultTypes AddNodeTransaction(NodeTransactionMessage message, NodeConnection connection)
        {
            var transaction = message.Transaction;

            var check = PreCheck(transaction);
            if (check != TransactionResultTypes.Ok)
                return check;

            if (transaction.IsCoreTransaction())
            {
                lock (_lock)
                    _coreTransactions.Add(new CoreItem(message.Hops, SenderTypes.Node, transaction as CoreTransaction, connection));
                return TransactionResultTypes.Ok;
            }

            if (transaction.IsServiceTransaction())
            {
                lock (_lock)
                    _serviceTransactions.Add(new ServiceItem(message.Hops, SenderTypes.Node, transaction as ServiceTransaction, connection));
            }

            if (transaction.IsDataTransaction())
            {
                lock (_lock)
                    _dataTransactions.Add(new DataItem(message.Hops, SenderTypes.Node, transaction as DataTransaction, message.Payload?.Validation, connection));
                return TransactionResultTypes.Ok;
            }

            if(transaction.IsMaintainTransaction())
            {
                lock (_lock)
                    _maintainTransactions.Add(new MaintainItem(message.Hops, transaction as MaintainTransaction, connection));
                return TransactionResultTypes.Ok;
            }

            return TransactionResultTypes.InvalidTransaction;
        }

        public TransactionResultTypes AddClientTransaction(ClientTransactionMessage message, ClientConnection connection)
        {
            var transaction = message.Transaction;
            var requestCode = message.RequestCode;
            var sendResponse = requestCode != 0;

            var check = PreCheck(transaction);
            if (check != TransactionResultTypes.Ok)
                return check;

            if (transaction.IsCoreTransaction())
            {
                lock (_lock)
                    _coreTransactions.Add(new CoreItem(message.RequestCode, SenderTypes.Client, transaction as CoreTransaction, connection));
                return TransactionResultTypes.Ok;
            }
            if (transaction.IsServiceTransaction())
            {
                lock (_lock)
                    _serviceTransactions.Add(new ServiceItem(message.RequestCode, SenderTypes.Client, transaction as ServiceTransaction, connection));
                return TransactionResultTypes.Ok;
            }
            if (transaction.IsDataTransaction())
            {
                lock (_lock)
                    _dataTransactions.Add(new DataItem(message.RequestCode, SenderTypes.Client, transaction as DataTransaction, null, connection));
                return TransactionResultTypes.Ok;
            }

            return TransactionResultTypes.InvalidTransaction;
        }
    }
}
