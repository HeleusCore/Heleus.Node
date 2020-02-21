using System;
using System.Collections.Generic;

namespace Heleus.Network
{
    public class NodeAutoConnect
    {
        public readonly Uri EndPoint;
        public NodeInfo NodeInfo;

        public NodeAutoConnect(Uri endPoint)
        {
            EndPoint = endPoint;
        }
    }
}
