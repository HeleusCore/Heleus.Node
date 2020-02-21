using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Chain.Blocks;
using Heleus.Cryptography;
using Heleus.Manager;
using Heleus.Messages;
using Heleus.Network.Client;
using Heleus.Node.Configuration;
using Heleus.Operations;
using Heleus.Service;
using Heleus.Service.Push;
using Heleus.Transactions;

namespace Heleus.Network.Server
{
    public class ClientServer : IServer, IServiceRemoteHost, IMessageReceiver<ClientConnection>
    {
        readonly object _lock = new object();
        readonly protected CancellationTokenSource _stopToken = new CancellationTokenSource();

        readonly Dictionary<long, ClientConnection> _connections = new Dictionary<long, ClientConnection>();

        class ClientResponseData
        {
            public readonly long RequestCode;
            public readonly long ConnectionId;
            public readonly long TransactionIdentifier;

            public ClientConnection Connection;
            public Operation Operation;
            public Transaction Transaction;

            public ClientResponseData(long requestCode, long connectionid, long transactionIdentifier)
            {
                RequestCode = requestCode;
                ConnectionId = connectionid;
                TransactionIdentifier = transactionIdentifier;
            }
        }

        class KeyWatch
        {
            public KeyCheck KeyCheck;
            public long ConnectionId;
            public ClientConnection Connection;
        }


        readonly LazyLookupTable<long, ClientResponseData> _clientResponses = new LazyLookupTable<long, ClientResponseData> { LifeSpan = TimeSpan.FromSeconds(15) };
        readonly LazyLookupTable<long, long> _keyWatches = new LazyLookupTable<long, long> { LifeSpan = TimeSpan.FromMinutes(10) };

        readonly ChainManager _chainManager;
        readonly AttachementManager _attachementManager;

        readonly PubSub _pubsub;
        readonly NodeConfiguration _configuration;
        readonly TransactionManager _transactionManager;

        static ClientServer()
        {
            ClientMessage.RegisterClientMessages();
            SystemMessage.RegisterSystemMessages();
        }

        public ClientServer(Node.Node node)
        {
            _chainManager = node.ChainManager;
            _configuration = node.NodeConfiguration;
            _attachementManager = node.AttachementManager;
            _transactionManager = node.TransactionManager;
            _pubsub = node.PubSub;

            _pubsub.Subscribe<BlockEvent<CoreBlock>>(this, NewCoreBlock);
            _pubsub.Subscribe<BlockEvent<ServiceBlock>>(this, NewServiceBlock);
            _pubsub.Subscribe<BlockEvent<DataBlock>>(this, NewDataBlock);
        }

        public Task Start()
        {
            return Task.CompletedTask;
        }

        public Task Stop()
        {
            _stopToken.Cancel();
            return Task.CompletedTask;
        }

        public async Task NewConnection(WebSocket webSocket)
        {
            if (_stopToken.IsCancellationRequested)
                return;

            var connection = new ClientConnection(webSocket) { ConnectionClosedEvent = ConnectionClosed };
            lock (_lock)
            {
                _connections[connection.ConnectionId] = (connection);
            }

            await connection.Receive(this);
        }

        public async Task HandleMessage(ClientConnection connection, Message message, ArraySegment<byte> rawData)
        {
            if (_stopToken.IsCancellationRequested)
                return;

            if (!message.IsSystemMessage())
            {
                if (connection.ClientInfo == null && message.MessageType != (ushort)ClientMessageTypes.ClientInfo)
                {
                    await connection.Close(DisconnectReasons.ProtocolError);
                    return;
                }

                if (connection.ClientInfo != null)
                {
                    if (connection.ClientInfo.AccountId <= 0 && message.MessageType != (ushort)ClientMessageTypes.ClientInfo)
                    {
                        if (!(message.MessageType == (ushort)ClientMessageTypes.KeyCheck || (message.MessageType == (ushort)ClientMessageTypes.Transaction && (message as ClientTransactionMessage).Transaction is AccountRegistrationCoreTransaction)))
                        {
                            await connection.Close(DisconnectReasons.ProtocolError);
                            return;
                        }
                    }

                    if (!message.IsMessageValid(connection.ClientInfo.ConnectionPublicKey))
                    {
                        await connection.Close(DisconnectReasons.ProtocolError);
                        return;
                    }
                }
            }
            else
            {
                var messageType = (SystemMessageTypes)message.MessageType;
                if (messageType == SystemMessageTypes.Disconnect)
                {
                    await connection.Close((message as SystemDisconnectMessage).DisconnectReason);
                }

                return;
            }

            if (message.IsClientMessage())
            {
                var messageType = (ClientMessageTypes)message.MessageType;
                if (messageType == ClientMessageTypes.ClientInfo)
                {
                    await OnClientInfo(message as ClientInfoMessage, connection);
                }
                else if (messageType == ClientMessageTypes.Transaction)
                {
                    await OnClientTransaction(message as ClientTransactionMessage, connection);
                }
                else if (messageType == ClientMessageTypes.AttachementsRequest)
                {
                    await OnAttachementsRequest(message as ClientAttachementsRequestMessage, connection);
                }
                else if (messageType == ClientMessageTypes.KeyCheck)
                {
                    await OnKeyCheck(message as ClientKeyCheckMessage, connection);
                }
                else if (messageType == ClientMessageTypes.Balance)
                {
                    await OnBalance(message as ClientBalanceMessage, connection);
                }
                else if (messageType == ClientMessageTypes.RemoteRequest || messageType == ClientMessageTypes.ErrorReport || messageType == ClientMessageTypes.PushToken || messageType == ClientMessageTypes.PushSubscription)
                {
                    await OnService(message as ClientServiceBaseMessage, connection);
                }
            }
            else
            {
                await connection.Close(DisconnectReasons.ProtocolError);
            }
        }

        async Task OnClientInfo(ClientInfoMessage message, ClientConnection connection)
        {
            var clientInfo = message.ClientInfo;

            if (!message.IsMessageValid(clientInfo.ConnectionPublicKey))
            {
                await connection.Close(DisconnectReasons.ProtocolError);
                return;
            }

            connection.ClientInfo = clientInfo;
            if (message.RequestCode != 0)
            {
                if (connection.Token == null)
                    connection.Token = Rand.NextSeed(16);

                await connection.Send(new ClientInfoResponseMessage(message.RequestCode, connection.Token) { SignKey = _configuration.LocaleNodePrivateKey });
            }
        }

        async Task OnService(ClientServiceBaseMessage message, ClientConnection connection)
        {
            var accountId = connection.ClientInfo.AccountId;
            var serviceHost = _chainManager.GetServiceHost(message.TargetChainId);
            if (serviceHost != null)
            {
                Key publicKey = null;
                if (message.ClientKeyIndex == Protocol.CoreAccountSignKeyIndex)
                {
                    var coreAccount = _chainManager.CoreChain.GetCoreAccount(accountId);
                    if (coreAccount != null)
                    {
                        publicKey = coreAccount.AccountKey;
                    }
                }
                else
                {
                    var chain = _chainManager.GetServiceChain(message.TargetChainId);
                    publicKey = chain?.GetValidServiceAccountKey(accountId, message.ClientKeyIndex, Time.Timestamp)?.PublicKey;
                }

                if (publicKey != null)
                {
                    if (message.MessageType == ClientMessageTypes.RemoteRequest && serviceHost.ClientMessageReceiver != null)
                    {
                        var m = message as ClientRemoteRequestMessage;
                        var serviceMessage = m.ClientData.GetSignedData(publicKey);
                        if (serviceMessage != null)
                        {
                            await serviceHost.ClientMessageReceiver.RemoteRequest(m.RemoteMessageType, serviceMessage, new ServiceRemoteRequest(connection, this, accountId, m.RequestCode));
                        }
                    }
                    else if (message.MessageType == ClientMessageTypes.ErrorReport && serviceHost.ErrorReportReceiver != null)
                    {
                        var m = message as ClientErrorReportMessage;
                        var serviceMessage = m.ClientData.GetSignedData(publicKey);
                        if (serviceMessage != null)
                        {
                            await serviceHost.ErrorReportReceiver.ClientErrorReports(accountId, serviceMessage);
                            if (message.RequestCode != 0)
                                await connection.Send(new ClientErrorReportResponseMessage(message.RequestCode));
                        }
                    }
                    else if (message.MessageType == ClientMessageTypes.PushToken && serviceHost.PushReceiver != null)
                    {
                        var m = message as ClientPushTokenMessage;
                        var tokenInfo = m.GetPushTokenInfo(publicKey);
                        if (tokenInfo != null)
                        {
                            var remote = new ServiceRemoteRequest(connection, this, accountId, m.RequestCode);
                            await serviceHost.PushReceiver.PushTokenInfo(m.TokenMessageAction, tokenInfo, remote);
                        }
                    }
                    else if (message.MessageType == ClientMessageTypes.PushSubscription && serviceHost.PushReceiver != null)
                    {
                        var m = message as ClientPushSubscriptionMessage;
                        var subscription = m.GetClientDataItem(publicKey);
                        if (subscription != null && subscription.AccountId == accountId)
                        {
                            await serviceHost.PushReceiver.PushSubscription(subscription, new ServiceRemoteRequest(connection, this, accountId, m.RequestCode));
                        }
                    }

                    return;
                }
            }

            await connection.Close(DisconnectReasons.ProtocolError);
        }

        async Task OnBalance(ClientBalanceMessage message, ClientConnection connection)
        {
            var coreAccount = _chainManager.CoreChain.GetCoreAccount(connection.ClientInfo.AccountId, false);
            if (coreAccount != null)
            {
                var token = message.SignedToken.GetSignedData(coreAccount.AccountKey);
                if (token != null && token.SequenceEqual(connection.Token))
                {
                    if (message.RequestCode != 0)
                        await connection.Send(new ClientBalanceResponseMessage(message.RequestCode, coreAccount.HeleusCoins) { SignKey = _configuration.LocaleNodePrivateKey });
                    return;
                }
            }

            await connection.Close(DisconnectReasons.ProtocolError);
        }

        async Task OnKeyCheck(ClientKeyCheckMessage message, ClientConnection connection)
        {
            if (message.AddWatch)
                _keyWatches.Add(message.KeyUniqueIdentifier, connection.ConnectionId);

            var check = _chainManager.CoreChain.BlockStorage.History.GetRegistrationJoinHistory(message.KeyUniqueIdentifier);
            if (message.RequestCode != 0)
                await connection.Send(new ClientKeyCheckResponseMessage(message.RequestCode, check) { SignKey = _configuration.LocaleNodePrivateKey });
        }

        async Task OnClientTransaction(ClientTransactionMessage message, ClientConnection connection)
        {
            var check = _transactionManager.AddClientTransaction(message, connection);

            var transaction = message.Transaction;
            var requestCode = message.RequestCode;

            if (requestCode != 0)
            {
                if (check == TransactionResultTypes.Ok)
                {
                    var identifier = transaction.UniqueIdentifier;
                    _clientResponses[identifier] = new ClientResponseData(requestCode, connection.ConnectionId, identifier);
                }
                else
                {
                    await connection.Send(new ClientTransactionResponseMessage(requestCode, check, null) { SignKey = _configuration.LocaleNodePrivateKey });
                }
            }
        }

        async Task OnAttachementsRequest(ClientAttachementsRequestMessage message, ClientConnection connection)
        {
            var result = TransactionResultTypes.Unknown;
            var userCode = 0L;
            var attachementKey = 0;

            var container = _chainManager.GetContainer(message.ChainId);
            var dataChain = container?.GetDataChain(message.ChainIndex);
            var serviceChain = container?.ServiceChain;
            var service = container?.Service;

            if (container == null || dataChain == null || serviceChain == null)
            {
                result = TransactionResultTypes.ChainNodeInvalid;
                goto end;
            }

            if (service == null)
            {
                result = TransactionResultTypes.ChainServiceUnavailable;
                goto end;
            }

            attachementKey = dataChain.AttachementKey;
            if (attachementKey < 0)
            {
                result = TransactionResultTypes.AttachementsNotAllowed;
                goto end;
            }

            Key key = null;
            if (message.KeyIndex == Protocol.CoreAccountSignKeyIndex)
            {
                var coreAccount = _chainManager.CoreChain.GetCoreAccount(message.AccountId);
                key = coreAccount?.AccountKey;
            }
            else
            {
                var chainAccount = serviceChain.GetValidServiceAccountKey(message.AccountId, message.KeyIndex, Time.Timestamp);
                key = chainAccount?.PublicKey;
            }

            if (key == null)
            {
                result = TransactionResultTypes.InvalidServiceAccountKey;
                goto end;
            }

            var attachements = message.Attachements.GetSignedItem(key);
            if (attachements == null)
            {
                result = TransactionResultTypes.InvalidSignature;
                goto end;
            }

            if (!attachements.CheckAttachements() || AttachementInfo.IsExpired(attachements))
            {
                result = TransactionResultTypes.AttachementsInvalid;
                goto end;
            }

            var valid = await service.IsValidAttachementsRequest(attachements);
            userCode = valid.UserCode;
            if (!valid.IsOK)
            {
                result = TransactionResultTypes.ChainServiceErrorResponse;
                goto end;
            }

            if (!_attachementManager.AddAttachementsRequests(attachements))
            {
                result = TransactionResultTypes.AttachementsInvalid;
                goto end;
            }

            // everything seems to be OK
            result = TransactionResultTypes.Ok;

        end:
            await connection.Send(new ClientAttachementsResponseMessage(message, result, userCode, attachementKey));
        }

        Task NewServiceBlock(BlockEvent<ServiceBlock> evt)
        {
            var block = evt.Block;

            var responses = new List<ClientResponseData>();
            var watches = new List<KeyWatch>();

            foreach (var transaction in block.Transactions)
            {
                if (_clientResponses.TryGetValue(transaction.UniqueIdentifier, out var responseData))
                {
                    _clientResponses.Remove(transaction.UniqueIdentifier);
                    responseData.Transaction = transaction;
                    responses.Add(responseData);
                }

                if (transaction.TransactionType == ServiceTransactionTypes.Join)
                {
                    var joinTransaction = transaction as JoinServiceTransaction;
                    var key = joinTransaction.AccountKey?.PublicKey;
                    if (key != null)
                    {
                        var uid = BitConverter.ToInt64(key.RawData.Array, key.RawData.Offset);
                        if (_keyWatches.TryGetValue(uid, out var cid))
                        {
                            watches.Add(new KeyWatch { ConnectionId = cid, KeyCheck = new KeyCheck(joinTransaction.AccountId, joinTransaction.TargetChainId, joinTransaction.AccountKey.KeyIndex, uid) });
                        }
                    }
                }
            }

            ProcessResponses(responses, watches);

            return Task.CompletedTask;
        }

        Task NewDataBlock(BlockEvent<DataBlock> evt)
        {
            var block = evt.Block;
            var responses = new List<ClientResponseData>();

            foreach (var transaction in block.Transactions)
            {
                if (_clientResponses.TryGetValue(transaction.UniqueIdentifier, out var responseData))
                {
                    _clientResponses.Remove(transaction.UniqueIdentifier);
                    responseData.Transaction = transaction;
                    responses.Add(responseData);
                }
            }

            ProcessResponses(responses, null);

            return Task.CompletedTask;
        }

        Task NewCoreBlock(BlockEvent<CoreBlock> evt)
        {
            var block = evt.Block;
            var responses = new List<ClientResponseData>();
            var watches = new List<KeyWatch>();

            foreach (var transaction in block.Transactions)
            {
                var transactionId = transaction.TransactionId;

                if (_clientResponses.TryGetValue(transaction.UniqueIdentifier, out var responseData))
                {
                    _clientResponses.Remove(transaction.UniqueIdentifier);
                    foreach (var item in block.Items)
                    {
                        var operation = item.Transaction;
                        if (transactionId == operation.OperationId)
                        {
                            responseData.Operation = operation;
                            responseData.Transaction = transaction;
                            responses.Add(responseData);

                            break;
                        }
                    }
                }
            }

            foreach (var op in block.Items)
            {
                if (op.Transaction.CoreOperationType == CoreOperationTypes.Account)
                {
                    var reg = op.Transaction as AccountOperation;
                    var key = reg.PublicKey;
                    var uid = BitConverter.ToInt64(key.RawData.Array, key.RawData.Offset);

                    if (_keyWatches.TryGetValue(uid, out var cid))
                    {
                        watches.Add(new KeyWatch { ConnectionId = cid, KeyCheck = new KeyCheck(reg.AccountId, Protocol.CoreChainId, Protocol.CoreAccountSignKeyIndex, uid) });
                    }
                }
            }

            ProcessResponses(responses, watches);

            return Task.CompletedTask;
        }

        void ProcessResponses(List<ClientResponseData> responses, List<KeyWatch> watches)
        {
            lock (_lock)
            {
                if (responses != null)
                {
                    foreach (var response in responses)
                    {
                        _connections.TryGetValue(response.ConnectionId, out response.Connection);
                    }
                }

                if (watches != null)
                {
                    foreach (var watch in watches)
                    {
                        _connections.TryGetValue(watch.ConnectionId, out watch.Connection);
                    }
                }
            }

            if (responses != null)
            {
                foreach (var response in responses)
                {
                    if (response.Connection != null)
                    {
                        _ = response.Connection.Send(new ClientTransactionResponseMessage(response.RequestCode, TransactionResultTypes.Ok, response.Operation ?? response.Transaction) { SignKey = _configuration.LocaleNodePrivateKey });
                    }
                }
            }

            if (watches != null)
            {
                foreach (var watch in watches)
                {
                    if (watch.Connection != null)
                    {
                        _ = watch.Connection.Send(new ClientKeyCheckResponseMessage(0, watch.KeyCheck) { SignKey = _configuration.LocaleNodePrivateKey });
                    }
                }
            }
        }

        void ConnectionClosed(ClientConnection connection, string reason)
        {
            lock (_lock)
            {
                _connections.Remove(connection.ConnectionId);
            }
        }

        public Task SendRemoteResponse(long messageType, byte[] messageData, IServiceRemoteRequest request)
        {
            try
            {
                if (request is ServiceRemoteRequest remoteRequest)
                {
                    var connection = remoteRequest.GetClientConnection();
                    if (connection != null)
                    {
                        _ = connection.Send(new ClientRemoteResponseMessage(messageType, messageData, request.RequestCode) { SignKey = _configuration.LocaleNodePrivateKey });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.IgnoreException(ex);
            }

            return Task.CompletedTask;
        }

        public Task SendPushSubscriptionResponse(PushSubscriptionResponse response, IServiceRemoteRequest request)
        {
            try
            {
                if (request is ServiceRemoteRequest remoteRequest)
                {
                    var connection = remoteRequest.GetClientConnection();
                    if (connection != null)
                    {
                        _ = connection.Send(new ClientPushSubscriptionResponseMessage(response, request.RequestCode) { SignKey = _configuration.LocaleNodePrivateKey });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.IgnoreException(ex);
            }

            return Task.CompletedTask;
        }

        public Task SendPushTokenResponse(PushTokenResult result, IServiceRemoteRequest request)
        {
            try
            {
                if (request is ServiceRemoteRequest remoteRequest)
                {
                    var connection = remoteRequest.GetClientConnection();
                    if (connection != null)
                    {
                        _ = connection.Send(new ClientPushTokenResponseMessage(result, request.RequestCode) { SignKey = _configuration.LocaleNodePrivateKey });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.IgnoreException(ex);
            }
            return Task.CompletedTask;
        }
    }
}
