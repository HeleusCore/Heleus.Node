using System;
using Heleus.Cryptography;
using Heleus.Base;

namespace Heleus.Messages
{
    public enum KademliaMessageTypes
    {
        Ping = 11,
        Pong = 12,

        Query = 13,
        QueryResult = 14,
        Last
    }

    public abstract class KademliaMessage : Message
    {
        public long TimeStamp { get; private set; }

        public bool Expired
        {
            get
            {
                return Time.Timestamp > (TimeStamp + 30 * 1000); // 30 seconds
            }
        }

        // Only for incoming
        public bool IsValid(Key targetNodeKey)
        {
            return !Expired && targetNodeKey != null && IsMessageValid(targetNodeKey);
        }

        public new KademliaMessageTypes MessageType => (KademliaMessageTypes)base.MessageType;

        protected KademliaMessage(KademliaMessageTypes messageType) : base((ushort)messageType)
        {
        }

        protected override void Pack(Packer packer)
        {
            base.Pack(packer);
            TimeStamp = Time.Timestamp;
            packer.Pack(TimeStamp);
        }

        protected override void Unpack(Unpacker unpacker)
        {
            base.Unpack(unpacker);
            TimeStamp = unpacker.UnpackLong();
        }
    }

	public static class KademliaMessageExtension
    {
        public static bool IsKademliaMessage(this Message message)
        {
            return (message.MessageType >= (ushort)KademliaMessageTypes.Ping && message.MessageType <= (ushort)KademliaMessageTypes.Last);
        }
    }
}
