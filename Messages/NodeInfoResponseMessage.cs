using System;
using Heleus.Cryptography;
using Heleus.Node.Configuration;

namespace Heleus.Messages
{
	public class NodeInfoResponseMessage : NodeInfoMessageBase
    {
		public NodeInfoResponseMessage() : base(NodeMessageTypes.NodeInfoResponse)
        {
        }

		public NodeInfoResponseMessage(NodeConfiguration nodeConfiguration, Hash receiverNodeId) : base(NodeMessageTypes.NodeInfoResponse, nodeConfiguration, receiverNodeId)
        {
        }
    }
}
