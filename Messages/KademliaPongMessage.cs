using System;
using Heleus.Cryptography;
using Heleus.Base;

namespace Heleus.Messages
{
    public sealed class KademliaPongMessage : KademliaMessage
    {
        public KademliaPingMessage.Challenge PingChallengeData { get; private set; }
        public long PingTimeStamp;

        public KademliaPongMessage() : base(KademliaMessageTypes.Pong)
        {

        }

        public KademliaPongMessage(KademliaPingMessage pingMessage) : base(KademliaMessageTypes.Pong)
        {
            PingChallengeData = pingMessage.ChallengeData;
            PingTimeStamp = pingMessage.TimeStamp;
        }

        protected override void Pack(Packer packer)
        {
            base.Pack(packer);
            packer.Pack(PingTimeStamp);
            packer.Pack(PingChallengeData.Pack);
        }

        protected override void Unpack(Unpacker unpacker)
        {
            base.Unpack(unpacker);
            PingTimeStamp = unpacker.UnpackLong();
            PingChallengeData = new KademliaPingMessage.Challenge(unpacker);
        }
    }
}
