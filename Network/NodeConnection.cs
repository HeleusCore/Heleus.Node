using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Heleus.Messages;

namespace Heleus.Network
{
    public class NodeConnection : Connection
    {
        public NodeInfo NodeInfo;
        public HashSet<NodeConnection> ConnectionList;
        public bool OutgoingConnection;
        public NodeAutoConnect AutoConnect;

        public new Action<NodeConnection, string> ConnectionClosedEvent;

        public NodeConnection(Uri endPoint) : base(endPoint)
        {
            Init();
        }

        public NodeConnection(WebSocket webSocket) : base(webSocket)
        {
            Init();
        }

        public override Task Close(DisconnectReasons disconnectReason)
        {
            return base.Close(disconnectReason);
        }

        void Init()
        {
            base.ConnectionClosedEvent = (connection, reason) =>
            {
                ConnectionClosedEvent?.Invoke(this, reason);
            };
        }
    }
}
