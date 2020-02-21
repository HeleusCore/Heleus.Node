using System;
using System.Net.WebSockets;

namespace Heleus.Network
{
    public class ServiceConnection : Connection
    {
        public new Action<ServiceConnection, string> ConnectionClosedEvent;

        public ServiceConnection(Uri endPoint) : base(endPoint)
        {
            Init();
        }

        public ServiceConnection(WebSocket webSocket) : base(webSocket)
        {
            Init();
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
