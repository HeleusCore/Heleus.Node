using System;
using System.Net.WebSockets;
using System.Threading.Tasks;

namespace Heleus.Network.Server
{
    public interface IServer
    {
        Task NewConnection(WebSocket webSocket);
    }
}
