using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Cryptography;
using Heleus.Manager;
using Heleus.Messages;
using Heleus.Network.Client;

namespace Heleus.Network.Server
{
    public class NodeServer : IServer, IMessageReceiver<NodeConnection>, ILogger
    {
        readonly protected object _lock = new object();

        readonly Kademlia.Kademlia _kademlia;
        readonly ChainManager _chainManager;
        readonly TransactionManager _transactionManager;
        readonly Node.Node _node;

        readonly HashSet<NodeConnection> _pendingConnections = new HashSet<NodeConnection>();
        readonly HashSet<NodeConnection> _incomingConnections = new HashSet<NodeConnection>();
        readonly HashSet<NodeConnection> _outgoingConnections = new HashSet<NodeConnection>();
        readonly List<HashSet<NodeConnection>> _connections = new List<HashSet<NodeConnection>>();

        readonly List<NodeAutoConnect> _autoConnect = new List<NodeAutoConnect>();
        readonly HashSet<Hash> _trustedNodeIds = new HashSet<Hash>();

        readonly int _maxIncomingConnections;
        readonly int _maxOutgoingConnections;

        static NodeServer()
        {
            Message.RegisterMessage<NodeInfoMessage>();
            Message.RegisterMessage<NodeInfoResponseMessage>();

            Message.RegisterMessage<NodeTransactionMessage>();
            Message.RegisterMessage<NodeBlockDataMessage>();
            Message.RegisterMessage<NodeBlockDataRequestMessage>();

            Message.RegisterMessage<CouncilBlockProposalMessage>();
            Message.RegisterMessage<CouncilBlockVoteMessage>();
            Message.RegisterMessage<CouncilBlockSignatureMessage>();
            Message.RegisterMessage<CouncilCurrentRevisionMessage>();
        }

        public NodeServer(Node.Node node, int maxIncomingConnections, int maxOutgoingConnections)
        {
            _kademlia = node.Kademlia;
            _chainManager = node.ChainManager;
            _transactionManager = node.TransactionManager;
            _node = node;

            _maxIncomingConnections = maxIncomingConnections;
            _maxOutgoingConnections = maxOutgoingConnections;

            _connections.Add(_pendingConnections);
            _connections.Add(_incomingConnections);
            _connections.Add(_outgoingConnections);

            foreach (var ac in node.NodeConfiguration.AutoConnectNodes)
            {
                _autoConnect.Add(new NodeAutoConnect(ac));
            }

            foreach (var id in node.NodeConfiguration.TrustedNodeIds)
            {
                _trustedNodeIds.Add(id);
            }
        }

        public Task Start()
        {
            TaskRunner.Run(() => Loop());
            return Task.CompletedTask;
        }

        public async Task Stop()
        {
            var tasks = new List<Task>();

            lock (_lock)
            {
                foreach (var list in _connections)
                {
                    foreach (var connection in list)
                    {
                        try
                        {
                            tasks.Add(connection.Close(DisconnectReasons.Graceful));
                        }
                        catch (Exception ex)
                        {
                            Log.IgnoreException(ex, this);
                        }
                    }
                }
            }

            await Task.WhenAll(tasks);
        }

        public bool IsConnected(Hash nodeId)
        {
            lock (_lock)
            {
                foreach (var list in _connections)
                {
                    foreach (var connection in list)
                    {
                        if (connection.NodeInfo != null && connection.NodeInfo.NodeId == nodeId)

                            return true;
                    }
                }
            }

            return false;
        }

        public bool IsConnected(NodeInfo nodeInfo, bool includePending)
        {
            lock (_lock)
            {
                foreach (var list in _connections)
                {
                    if (!includePending && list == _pendingConnections)
                        continue;

                    foreach (var connection in list)
                    {
                        if (connection.NodeInfo?.NodeId == nodeInfo.NodeId)
                            return true;
                    }
                }
            }

            return false;
        }

        public int ConnectionCount
        {
            get
            {
                var count = 0;
                lock (_lock)
                {
                    foreach (var list in _connections)
                    {
                        count += list.Count;
                    }
                }
                return count;
            }
        }

        public string LogName => GetType().Name;

        public bool IsPending(NodeConnection connection)
        {
            lock (_lock)
                return connection.ConnectionList == _pendingConnections;
        }

        bool IsCoreNode(NodeInfo nodeInfo)
        {
            var nodeKey = nodeInfo.CoreNodeKey;
            if (nodeKey != null)
            {
                var key = _chainManager.CoreChain.GetValidPublicChainKey(nodeKey.ChainId, nodeKey.ChainIndex, nodeKey.KeyIndex, Time.Timestamp);
                return nodeKey.IsSignatureValid(key?.PublicKey);
            }

            return false;
        }

        bool IsCoreNodeMessage(NodeInfoMessageBase message)
        {
            if (message.IsCoreNode)
            {
                var nodeKey = message.NodeInfo.CoreNodeKey;
                if (nodeKey != null)
                {
                    var key = _chainManager.CoreChain.GetValidPublicChainKey(nodeKey.ChainId, nodeKey.ChainIndex, nodeKey.KeyIndex, Time.Timestamp);
                    return message.IsCoreVoteSignatureValid(key?.PublicKey);
                }
            }
            return false;
        }

        async Task Loop()
        {
            while (!_node.HasQuit)
            {
                try
                {
                    var count = 0;
                    for (var i = 0; i < 5; i++)
                    {
                        var nodeInfo = _kademlia.GetRandomNode();
                        if (nodeInfo != null)
                        {
                            if (!IsConnected(nodeInfo.NodeId))
                                await Connect(nodeInfo);
                            count++;
                        }
                    }

                    if (count == 0 && ConnectionCount <= 2)
                    {
                        foreach (var beacon in _node.NodeConfiguration.BeaconNodes)
                        {
                            var client = new NodeClient(beacon);
                            var nodeInfo = (await client.DownloadNodeInfo(_kademlia.NetworkKey)).Data;
                            if (nodeInfo != null && !IsConnected(nodeInfo.NodeId))
                            {
                                await Connect(nodeInfo, beacon);
                            }
                        }
                    }

                    foreach (var item in _autoConnect)
                    {
                        if (item.NodeInfo == null)
                        {
                            var client = new NodeClient(item.EndPoint);
                            item.NodeInfo = (await client.DownloadNodeInfo(_kademlia.NetworkKey)).Data;

                            if (item.NodeInfo != null)
                            {
                                lock (_lock)
                                {
                                    _trustedNodeIds.Add(item.NodeInfo.NodeId);
                                }
                            }
                        }

                        if (item.NodeInfo != null)
                        {
                            if (!IsConnected(item.NodeInfo, true))
                                await Connect(item.NodeInfo, item.EndPoint);
                        }
                    }

                    var close = new List<NodeConnection>();
                    lock (_lock)
                    {
                        foreach (var incoming in _incomingConnections)
                        {
                            foreach (var outgoing in _outgoingConnections)
                            {
                                if (incoming.NodeInfo.NodeId == outgoing.NodeInfo.NodeId)
                                {
                                    close.Add(incoming);
                                }
                            }
                        }
                    }

                    foreach (var conn in close)
                        await conn.Close(DisconnectReasons.AlreadyConnected);

                    await Task.Delay(Time.Seconds(5), _node.QuitToken);
                    if (_node.HasQuit)
                        return;
                }
                catch (Exception ex)
                {
                    Log.IgnoreException(ex, this);
                }
            }
        }

        public async Task Connect(NodeInfo nodeInfo, Uri endPoint = null)
        {
            if (_node.HasQuit)
                return;

            if (nodeInfo == null)
                return;

            var accept = false;
            lock (_lock)
                accept = _outgoingConnections.Count <= _maxOutgoingConnections;

            if (!accept)
                return;

            if (IsConnected(nodeInfo, true))
                return;

            if (endPoint == null)
                endPoint = nodeInfo.PublicEndPoint;

            var client = new NodeClient(endPoint);

            var connection = await client.OpenNodeConnection();
            if (connection.Connected)
            {
                if (Log.LogTrace)
                    Log.Trace($"NodeServer ({_kademlia.LocalNodeInfo.PublicEndPoint}) connecting {nodeInfo.PublicEndPoint}");

                connection.NodeInfo = nodeInfo;
                connection.OutgoingConnection = true;
                connection.ConnectionClosedEvent = ConnectionClosed;
                connection.ConnectionList = _pendingConnections;

                lock (_lock)
                    _pendingConnections.Add(connection);

                TaskRunner.Run(() => connection.Receive(this));

                await connection.Send(new NodeInfoMessage(_kademlia.NodeConfiguration, nodeInfo.NodeId) { SignKey = _kademlia.LocalKey });
            }
        }

        public async Task NewConnection(WebSocket webSocket)
        {
            if (_node.HasQuit)
                return;

            //if (Log.LogTrace)
            //Log.Trace("NodeServer ({0}) new connection", _kademlia.LocalNodeInfo.PublicEndPoint);

            var connection = new NodeConnection(webSocket) { OutgoingConnection = false, ConnectionList = _pendingConnections, ConnectionClosedEvent = ConnectionClosed };
            lock (_lock)
                _pendingConnections.Add(connection);

            await connection.Receive(this);
        }

        public async Task HandleMessage(NodeConnection connection, Message message, ArraySegment<byte> rawData)
        {
            if (_node.HasQuit)
                return;

            if (!message.IsSystemMessage())
            {
                if (connection.NodeInfo == null && message.MessageType != (ushort)NodeMessageTypes.NodeInfo)
                {
                    Log.Warn($"Invalid message received {message.GetType().Name}.");
                    await connection.Close(DisconnectReasons.ProtocolError);
                    return;
                }

                if (connection.NodeInfo != null)
                {
                    if (!message.IsMessageValid(connection.NodeInfo.NodeKey))
                    {
                        Log.Warn($"Invalid message received {message.GetType().Name}.");
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
                    await OnDisconnect(message as SystemDisconnectMessage, connection);
                }

                return;
            }

            if (message.IsNodeMessage())
            {
                var messageType = (NodeMessageTypes)message.MessageType;
                switch (messageType)
                {
                    case NodeMessageTypes.NodeInfo:
                        await OnNodeInfo(message as NodeInfoMessage, connection);
                        break;
                    case NodeMessageTypes.NodeInfoResponse:
                        await OnNodeInfoResponse(message as NodeInfoResponseMessage, connection);
                        break;
                    case NodeMessageTypes.Transaction:
                        await OnNodeTransaction(message as NodeTransactionMessage, connection);
                        break;
                    case NodeMessageTypes.BlockData:
                        await OnBlockData(message as NodeBlockDataMessage, connection);
                        break;
                    case NodeMessageTypes.BlockDataRequest:
                        await OnBlockRequest(message as NodeBlockDataRequestMessage, connection);
                        break;
                }
            }
            else if (message.IsKademliaMessage())
            {
                await _kademlia.OnMessage(connection, connection.NodeInfo, message as KademliaMessage);
            }
            else if (message.IsCouncilMessage() && message is CouncilMessage councilMessage)
            {
                if (!await _node.CouncilManager.OnMessage(councilMessage, connection))
                {
                    Log.Warn($"Invalid council message received {message.GetType().Name}.");
                    await connection.Close(DisconnectReasons.ProtocolError);

                    return;
                }
            }
            else
            {
                Log.Warn($"Unrecognized message received {message.GetType().Name}.");
                await connection.Close(DisconnectReasons.ProtocolError);
            }
        }

        async Task OnDisconnect(SystemDisconnectMessage message, NodeConnection connection)
        {
            await connection.Close(message.DisconnectReason);
        }

        async Task OnNodeInfo(NodeInfoMessage message, NodeConnection connection)
        {
            var nodeInfo = message.NodeInfo;
            if (Log.LogTrace)
                Log.Trace($"NodeServer ({_kademlia.LocalNodeInfo.PublicEndPoint}) new connection from {nodeInfo.PublicEndPoint}");

            if (!message.IsMessageValid(message.NodeInfo.NodeKey))
            {
                Log.Warn($"Invalid message received {message.GetType().Name}.");
                await connection.Close(DisconnectReasons.ProtocolError);
                return;
            }

            if (message.ReceiverNodeId != _kademlia.LocalId)
            {
                Log.Warn($"NodeId invalid {message.GetType().Name}.");
                await connection.Close(DisconnectReasons.ProtocolError);
                return;
            }

            var accept = false;
            lock (_lock)
                accept = _incomingConnections.Count <= _maxIncomingConnections;

            var isCoreNode = IsCoreNode(connection.NodeInfo);
            if (isCoreNode)
            {
                if (!IsCoreNodeMessage(message))
                {
                    Log.Warn($"Invalid core node received {message.GetType().Name}.");
                    await connection.Close(DisconnectReasons.ProtocolError);

                    return;
                }
            }

            // core nodes are always accepted
            if (!accept && isCoreNode)
                accept = true;

            if (!accept)
            {
                foreach (var nodeId in _trustedNodeIds)
                {
                    if (nodeId == nodeInfo.NodeId)
                    {
                        accept = true;
                        break;
                    }
                }
            }

            if (!accept)
            {
                await connection.Send(new SystemDisconnectMessage(DisconnectReasons.ServerFull));
                await connection.Close(DisconnectReasons.ServerFull);

                return;
            }

            if (IsConnected(nodeInfo, false))
            {
                await connection.Send(new SystemDisconnectMessage(DisconnectReasons.AlreadyConnected));
                await connection.Close(DisconnectReasons.AlreadyConnected);

                return;
            }

            connection.NodeInfo = nodeInfo;

            if (!await HandleCouncilMember(connection))
                return;

            await connection.Send(new NodeInfoResponseMessage(_node.NodeConfiguration, _kademlia.LocalId) { SignKey = _kademlia.LocalKey });

            UpdateNewConnection(connection);
            await _kademlia.Ping(connection, connection.NodeInfo);
        }

        async Task OnNodeInfoResponse(NodeInfoResponseMessage message, NodeConnection connection)
        {
            connection.NodeInfo = message.NodeInfo;

            if (IsCoreNode(connection.NodeInfo))
            {
                if (!IsCoreNodeMessage(message))
                {
                    Log.Warn($"Invalid core node received {message.GetType().Name}.");
                    await connection.Close(DisconnectReasons.ProtocolError);
                    return;
                }
            }

            if (!await HandleCouncilMember(connection))
                return;

            UpdateNewConnection(connection);
            await _kademlia.Ping(connection, connection.NodeInfo);
        }

        void UpdateNewConnection(NodeConnection connection)
        {
            lock (_lock)
            {
                connection.ConnectionList.Remove(connection);
                connection.ConnectionList = connection.OutgoingConnection ? _outgoingConnections : _incomingConnections;
                connection.ConnectionList.Add(connection);
            }

            var block = _chainManager.CoreChain.BlockStorage.LastBlock;
            if (block != null)
                _ = connection.Send(new NodeBlockDataMessage(block.ChainType, block.BlockId, block.ChainId, block.ChainIndex) { SignKey = _kademlia.LocalKey });
        }

        async Task<bool> HandleCouncilMember(NodeConnection connection)
        {
            if (!_node.CouncilManager.NewNodeConnection(connection))
            {
                Log.Warn($"Invalid coucil node disconnected.");
                await connection.Close(DisconnectReasons.ProtocolError);
                return false;
            }
            return true;
        }

        Task OnNodeTransaction(NodeTransactionMessage message, NodeConnection connection)
        {
            _transactionManager.AddNodeTransaction(message, connection);
            return Task.CompletedTask;
        }

        async Task OnBlockData(NodeBlockDataMessage message, NodeConnection connection)
        {
            var chainType = message.ChainType;
            var blockId = message.BlockId;
            var chainId = message.ChainId;
            var chainIndex = message.ChainIndex;

            var chain = _chainManager.GetChain(chainType, chainId, chainIndex);

            if (chain == null || blockId <= chain.BlockStorage.LastStoredBlockId)
                return;

            if (message.BlockData != null)
            {
                await _node.SyncManager.HandleBlockData(message.BlockData, new HashSet<NodeConnection> { connection });
            }
            else
            {
                if (connection.NodeInfo.IsPublicEndPoint || connection.AutoConnect != null)
                {
                    var client = new NodeClient(connection.NodeInfo.IsPublicEndPoint ? connection.NodeInfo.PublicEndPoint : connection.AutoConnect.EndPoint);
                    var blockData = (await client.DownloadBlockData(chainType, chainId, chainIndex, blockId)).Data;

                    if (blockData != null)
                    {
                        await _node.SyncManager.HandleBlockData(blockData, new HashSet<NodeConnection> { connection });
                        return;
                    }
                }

                _ = connection.Send(new NodeBlockDataRequestMessage(chainType, blockId, chain.ChainId, chainIndex) { SignKey = _node.NodeConfiguration.LocaleNodePrivateKey });
            }
        }

        async Task OnBlockRequest(NodeBlockDataRequestMessage message, NodeConnection connection)
        {
            var chainType = message.ChainType;
            var blockid = message.BlockId;
            var chainId = message.ChainId;
            var chainIndex = message.ChainIndex;

            var chain = _chainManager.GetChain(chainType, chainId, chainIndex);

            if (chain == null)
                return;

            var blockData = await chain.BlockStorage.GetBlockData(blockid);
            if (blockData != null)
                await connection.Send(new NodeBlockDataMessage(chainType, blockid, chainId, chainIndex, blockData) { SignKey = _node.NodeConfiguration.LocaleNodePrivateKey });
        }

        public Task Broadcast(Message message, HashSet<NodeConnection> ignoredConnections)
        {
            ignoredConnections = ignoredConnections ?? new HashSet<NodeConnection>();

            message.ToByteArray(true);

            lock (_lock)
            {
                foreach (var list in _connections)
                {
                    foreach (var connection in list)
                    {
                        if (ignoredConnections.Contains(connection))
                            continue;

                        try
                        {
                            _ = connection.Send(message);
                        }
                        catch (Exception ex)
                        {
                            Log.IgnoreException(ex, this);
                        }
                    }
                }
            }

            return Task.CompletedTask;
        }

        void ConnectionClosed(NodeConnection connection, string reason)
        {
            if (Log.LogTrace)
                Log.Trace($"NodeServer ({_kademlia.LocalNodeInfo.PublicEndPoint}) connection closed {reason}");

            lock (_lock)
            {
                connection.ConnectionList?.Remove(connection);
            }

            _node.CouncilManager.NodeConnectionClosed(connection);
        }
    }
}
