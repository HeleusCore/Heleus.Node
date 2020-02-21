using System;
using System.Collections.Generic;
using Heleus.Cryptography;
using Heleus.Base;
using Heleus.Network;

namespace Heleus.Messages
{
    public sealed class KademliaQueryResultMessage : KademliaMessage
    {
        public List<NodeInfo> Nodes = new List<NodeInfo>();

        public KademliaQueryResultMessage() : base(KademliaMessageTypes.QueryResult)
        {
        }

        protected override void Pack(Packer packer)
        {
            base.Pack(packer);
            packer.Pack(Nodes.Count);
            foreach (var node in Nodes)
                packer.Pack(node.Pack);
        }

        protected override void Unpack(Unpacker unpacker)
        {
            base.Unpack(unpacker);
            unpacker.Unpack(out int count);
            for (var i = 0; i < count; i++)
                Nodes.Add(new NodeInfo(unpacker));
        }
    }
}
