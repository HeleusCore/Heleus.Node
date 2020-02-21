using System;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Heleus.Messages;

namespace Heleus.Network.Server
{
    public class ServiceServer : IServer, IMessageReceiver<ServiceConnection>
    {
        public ServiceServer()
        {
        }

        public Task Start()
        {
            return Task.CompletedTask;
        }

        public Task Stop()
        {
            return Task.CompletedTask;
        }

		public Task HandleMessage(ServiceConnection connection, Message message, ArraySegment<byte> rawData)
        {
            throw new NotImplementedException();
        }

        public Task NewConnection(WebSocket webSocket)
        {
            throw new NotImplementedException();
        }
    }
}
