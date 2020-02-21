using System;
using Heleus.Cryptography;

namespace Heleus.Messages
{
    public sealed class KademliaQueryMessage : KademliaMessage
    {
        public KademliaQueryMessage() : base(KademliaMessageTypes.Query)
        {

        }

    }
}
