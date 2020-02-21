using System;

namespace Heleus.Messages
{
    public enum NodeMessageTypes
    {
        NodeInfo = 21,
        NodeInfoResponse = 22,
        Transaction = 23,
        BlockData = 24,
        BlockDataRequest = 25,
        Last
    }
    
    public abstract class NodeMessage : Message
    {
        public new NodeMessageTypes MessageType => (NodeMessageTypes)base.MessageType;

        protected NodeMessage(NodeMessageTypes messageType) : base((ushort)messageType)
        {
        }
    }

    public static class NodeMessageExtension
    {
        public static bool IsNodeMessage(this Message message)
        {
            return (message.MessageType >= (ushort)NodeMessageTypes.NodeInfo && message.MessageType < (ushort)NodeMessageTypes.Last);
        }
    }
}
