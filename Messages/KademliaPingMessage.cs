using System;
using Heleus.Base;
using Heleus.Cryptography;

namespace Heleus.Messages
{
    public sealed class KademliaPingMessage : KademliaMessage
    {
        public class Challenge
        {
            public readonly byte[] ChallengeData;
            public readonly Signature Signature;

            public Challenge(Key signKey)
            {
                ChallengeData = Guid.NewGuid().ToByteArray();
                Signature = Signature.Generate(signKey, Hash.Generate(HashTypes.Sha512, ChallengeData));
            }

            public Challenge(Unpacker unpacker)
            {
                unpacker.Unpack(out ChallengeData);
                unpacker.Unpack(out Signature);
            }

            public bool IsValid(Key key)
            {
                return Signature.IsValid(key, Hash.Generate(HashTypes.Sha512, ChallengeData));
            }

            public void Pack(Packer packer)
            {
                packer.Pack(ChallengeData);
                packer.Pack(Signature);
            }
        }

        public Challenge ChallengeData { get; private set; }
        
        public KademliaPingMessage() : base(KademliaMessageTypes.Ping)
        {

        }

        public KademliaPingMessage(Challenge challenge) : base(KademliaMessageTypes.Ping)
        {
            ChallengeData = challenge;
        }

		protected override void Pack(Packer packer)
		{
            base.Pack(packer);
            packer.Pack(ChallengeData.Pack);
		}

		protected override void Unpack(Unpacker unpacker)
		{
            base.Unpack(unpacker);
            ChallengeData = new Challenge(unpacker);
		}
	}
}
