using System;
using Heleus.Cryptography;
using Heleus.Node;
using Heleus.Node.Configuration;

namespace Heleus.Messages
{
    public class NodeInfoMessage : NodeInfoMessageBase
    {
        public NodeInfoMessage() : base(NodeMessageTypes.NodeInfo)
        {
        }

        public NodeInfoMessage(NodeConfiguration nodeConfiguration, Hash receiverNodeId) : base(NodeMessageTypes.NodeInfo, nodeConfiguration, receiverNodeId)
        {
        }
    }
}
