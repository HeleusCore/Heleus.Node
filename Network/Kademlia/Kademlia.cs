using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Cryptography;
using Heleus.Messages;
using Heleus.Network.Client;
using Heleus.Node;
using Heleus.Node.Configuration;

namespace Heleus.Network.Kademlia
{
    public interface IMessageReceiver
    {
        Task<bool> HandleMessage(Message message);
    }

    public class Kademlia : ILogger
    {
        public string LogName => nameof(Kademlia);

        public readonly NodeConfiguration NodeConfiguration;
        public readonly NodeInfo LocalNodeInfo;
        public readonly Key LocalKey;
        public readonly Hash LocalId;
        readonly Node.Node _node;

        public readonly Key NetworkKey;

        readonly Storage _storage;
        readonly KBucket<NodeInfo> _bucket;
        readonly object _lock = new object();

        int _evictionsCount = 0;
        bool _started;
        int _discoverRounds = 1;
        List<NodeInfo> _queryNodes = new List<NodeInfo>();

        //TaskCompletionSource<bool> _startCompletion;

        static Kademlia()
        {
            Message.RegisterMessage<KademliaPingMessage>();
            Message.RegisterMessage<KademliaPongMessage>();
            Message.RegisterMessage<KademliaQueryMessage>();
            Message.RegisterMessage<KademliaQueryResultMessage>();
        }

        public Kademlia(Storage storage, Node.Node node)
        {
            _node = node;
            NodeConfiguration = node.NodeConfiguration;
            LocalKey = NodeConfiguration.LocaleNodePrivateKey;
            LocalNodeInfo = NodeConfiguration.LocaleNodeInfo;
            LocalId = LocalNodeInfo.NodeId;
            NetworkKey = NodeConfiguration.NetworkPublicKey;

            _storage = storage;
            _bucket = new KBucket<NodeInfo>(LocalId, 20);

            var nodesData = storage.ReadFileBytes("nodes.data");
            try
            {
                if (nodesData != null)
                {
                    using (var unpacker = new Unpacker(nodesData))
                    {
                        var c = unpacker.UnpackInt();
                        var timestamp = unpacker.UnpackLong();
                        if (Time.Timestamp < timestamp + Time.Hours(24)) // ignore if older than 24 hours
                        {
                            for (var i = 0; i < c; i++)
                            {
                                var nodeInfo = new NodeInfo(unpacker);
                                if (nodeInfo.IsSignatureValid)
                                    _queryNodes.Add(nodeInfo);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.IgnoreException(ex, this);
            }
        }

        public Task<bool> Start()
        {
            if (_started)
            {
                Log.Error("Node discovery already startet.", this);
                return Task.FromResult(false);
            }
            _started = true;

            Log.Info($"Starting node discovery with node id {LocalId.HexString}.", this);
            Log.Info($"Node key is {LocalKey.PublicKey.HexString}.", this);
            Log.Info($"Network key is {NetworkKey.HexString}.", this);

            TaskRunner.Run(async () =>
            {
                var beacons = NodeConfiguration.AutoConnectNodes.Concat(NodeConfiguration.BeaconNodes);
                if (beacons != null)
                {
                    foreach (var endpoint in beacons)
                        await QueryEndPoint(endpoint, true);
                }
            });

            TaskRunner.Run(() => DiscoveryLoop());
            TaskRunner.Run(() => PingOldestLoop());
            TaskRunner.Run(() => QueryLoop());

            return Task.FromResult(true);
        }

        public Task Stop()
        {
            return Task.CompletedTask;
        }

        public NodeInfo GetRandomNode()
        {
            return _bucket.GetRandomNode();
        }

        async Task<bool> QueryEndPoint(Uri endpoint, bool querySubNodes)
        {
            try
            {
                if (endpoint == LocalNodeInfo.PublicEndPoint)
                    return false;

                var client = new NodeClient(endpoint);
                var nodeInfoResult = (await client.DownloadNodeInfo());
                if (nodeInfoResult.ResultType == DownloadResultTypes.Timeout || nodeInfoResult.ResultType == DownloadResultTypes.NotFound)
                {
                    return false;
                }
                var nodeInfo = nodeInfoResult.Data;
                if (nodeInfo != null)
                {
                    if (nodeInfo.NetworkKey != NetworkKey)
                    {
                        Log.Info($"Queryied node not is invalid {nodeInfo.PublicEndPoint}. Wrong network key.", this);
                        return false;
                    }


                    {
                        if (nodeInfo.NodeId == LocalId)
                            return false;

                        var queryResult = (await client.DownloadKademliaNodes(nodeInfo.NodeKey, LocalId)).Data;
                        if (queryResult != null)
                        {
                            if (nodeInfo.IsPublicEndPoint)
                                AddValidNode(nodeInfo);
                            else
                                Log.Warn($"Node has no public endpoint {endpoint}.", this);

                            if (querySubNodes)
                            {
                                foreach (var node in queryResult.Nodes)
                                {
                                    if (node.IsPublicEndPoint)
                                    {
                                        var c2 = new NodeClient(node.PublicEndPoint);
                                        var ni2 = (await c2.DownloadNodeInfo()).Data;
                                        if (ni2 != null && ni2.NetworkKey == NetworkKey && node.NodeKey == ni2.NodeKey)
                                        {
                                            AddValidNode(ni2);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.IgnoreException(ex, this);
                Log.Error($"Node is invalid {endpoint}.", this);
            }
            return true;
        }

        async Task QueryLoop()
        {
            while (!_node.HasQuit)
            {
                await Task.Delay(Time.Seconds(5), _node.QuitToken);
                if (_node.HasQuit)
                    return;

                List<NodeInfo> nodes = null;
                lock (_queryNodes)
                {
                    if (_queryNodes.Count > 0)
                        nodes = new List<NodeInfo>(_queryNodes);
                    _queryNodes.Clear();
                }

                if (nodes != null)
                {
                    foreach (var node in nodes)
                    {
                        if (node.IsPublicEndPoint)
                        {
                            if (!await QueryEndPoint(node.PublicEndPoint, true))
                                _bucket.Remove(node.NodeId);
                        }
                    }
                }
            }
        }

        async Task DiscoveryLoop()
        {
            while (!_node.HasQuit)
            {
                if (_discoverRounds > 10)
                    await Task.Delay(Time.Minutes(60), _node.QuitToken); // do a discovery every hour
                else
                    await Task.Delay(Time.Seconds(4 * _discoverRounds), _node.QuitToken);
                if (_node.HasQuit)
                    return;

                try
                {
                    var nodes = _bucket.GetNearNodes(LocalId);
                    var count = 0;
                    foreach (var node in nodes)
                    {
                        if (node.IsPublicEndPoint)
                        {
                            if (!await QueryEndPoint(node.PublicEndPoint, true))
                                _bucket.Remove(node.NodeId);

                            if (count >= 5)
                                break;
                        }
                    }

                    nodes = _bucket.GetFarNodes();
                    count = 0;
                    foreach (var node in nodes)
                    {
                        if (node.IsPublicEndPoint)
                        {
                            if (!await QueryEndPoint(node.PublicEndPoint, true))
                                _bucket.Remove(node.NodeId);

                            if (count >= 5)
                                break;
                        }
                    }

                    var packedNodes = _bucket.PackNodes();
                    if (packedNodes != null)
                        _storage.WriteFileBytes("nodes.data", packedNodes);
                }
                catch (Exception ex)
                {
                    Log.IgnoreException(ex, this);
                }

                ++_discoverRounds;
            }
        }

        async Task PingOldestLoop()
        {
            while (!_node.HasQuit)
            {
                await Task.Delay(Time.Minutes(5), _node.QuitToken); // ping oldest every 5 minutes
                if (_node.HasQuit)
                    return;

                try
                {
                    var oldest = _bucket.GetOldestNodes();

                    foreach (var node in oldest)
                    {
                        if (node.IsPublicEndPoint)
                        {
                            if (!await QueryEndPoint(node.PublicEndPoint, false))
                                _bucket.Remove(node.NodeId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.IgnoreException(ex, this);
                }
            }
        }

        public List<NodeInfo> GetNearestNodes(Hash nodeId)
        {
            return _bucket.GetNearNodes(nodeId);
        }

        void AddValidNode(NodeInfo nodeInfo)
        {
            if (!nodeInfo.IsPublicEndPoint)
                return;

            if (nodeInfo.NodeId == LocalId)
                return;

            if (!_bucket.Contains(nodeInfo.NodeId))
                Log.Trace($"Adding new node {nodeInfo.PublicEndPoint}.", this);

            var evictNode = _bucket.AddOrUpdate(nodeInfo);
            if (evictNode != null)
            {
                lock (_lock)
                {
                    if (_evictionsCount > 8)
                        return;
                    _evictionsCount++;
                }
                TaskRunner.Run(async () =>
                {
                    var client = new NodeClient(evictNode.PublicEndPoint);
                    var ni = await client.DownloadNodeInfo();
                    if (ni.ResultType == DownloadResultTypes.Ok)
                    {
                        _bucket.AddOrUpdate(evictNode);
                    }
                    else
                    {
                        _bucket.Remove(evictNode.NodeId);
                        _bucket.AddOrUpdate(nodeInfo);
                    }

                    lock (_lock)
                    {
                        _evictionsCount--;
                    }
                });
            }
        }

        public async Task Ping(Connection connection, NodeInfo nodeInfo)
        {
            if (!nodeInfo.IsPublicEndPoint)
                return;

            if (Log.LogTrace)
                Log.Trace($"Kademlia Ping received from: {nodeInfo.PublicEndPoint}", this);

            await connection.Send(new KademliaPingMessage(new KademliaPingMessage.Challenge(LocalKey)) { SignKey = LocalKey });
        }

        async Task OnPing(KademliaPingMessage message, Connection connection, NodeInfo nodeInfo)
        {
            await connection.Send(new KademliaPongMessage(message) { SignKey = LocalKey });
        }

        Task OnPong(KademliaPongMessage message, Connection connection, NodeInfo nodeInfo)
        {
            if (Log.LogTrace)
                Log.Trace($"Kademlia Pong received from: {nodeInfo.PublicEndPoint}", this);

            var validChallenge = message.PingChallengeData.IsValid(LocalKey);
            if (validChallenge)
            {
                lock (_lock)
                    _queryNodes.Add(nodeInfo);
            }

            return Task.CompletedTask;
        }

        async Task OnQuery(KademliaQueryMessage message, Connection connection, NodeInfo nodeInfo)
        {
            if (Log.LogTrace)
                Log.Trace($"Kademlia Query received from: {nodeInfo.PublicEndPoint}", this);

            lock (_lock)
                _queryNodes.Add(nodeInfo);

            var nodes = _bucket.GetNearNodes(nodeInfo.NodeId);
            var resultMessage = new KademliaQueryResultMessage { SignKey = LocalKey };
            foreach (var node in nodes)
                resultMessage.Nodes.Add(node);

            await connection.Send(resultMessage);
        }

        Task OnQueryResult(KademliaQueryResultMessage message, Connection connection, NodeInfo nodeInfo)
        {
            if (Log.LogTrace)
                Log.Trace($"Kademlia QueryResult received from: {nodeInfo.PublicEndPoint}", this);

            lock (_lock)
            {
                _queryNodes.Add(nodeInfo);
                foreach (var node in message.Nodes)
                    _queryNodes.Add(node);
            }

            return Task.CompletedTask;
        }

        public Task OnMessage(Connection connection, NodeInfo nodeInfo, KademliaMessage message)
        {
            if (message.Expired || !nodeInfo.IsPublicEndPoint)
                return Task.CompletedTask;

            var messageType = message.MessageType;
            switch (messageType)
            {
                case KademliaMessageTypes.Ping:
                    return OnPing(message as KademliaPingMessage, connection, nodeInfo);
                case KademliaMessageTypes.Pong:
                    return OnPong(message as KademliaPongMessage, connection, nodeInfo);
                case KademliaMessageTypes.Query:
                    return OnQuery(message as KademliaQueryMessage, connection, nodeInfo);
                case KademliaMessageTypes.QueryResult:
                    return OnQueryResult(message as KademliaQueryResultMessage, connection, nodeInfo);
            }

            return Task.CompletedTask;
        }
    }
}
