using System;
using Heleus.Base;
using Heleus.Chain;
using Heleus.Cryptography;

namespace Heleus.Messages
{
    public enum CouncilMessageTypes
    {
        BlockProposal = 100,
        BlockVote = 101,
        BlockSignature = 102,
        CurrentRevision = 103,
        Last
    }
    
    public abstract class CouncilMessage : Message
    {
        public new CouncilMessageTypes MessageType => (CouncilMessageTypes)base.MessageType;

        public short Issuer { get; private set; }

        public ChainType ChainType { get; private set; }
        public int ChainId { get; private set; }
        public uint ChainIndex { get; private set; }

        readonly Key _signKey;
        Signature _signature;
        Hash _dataHash;

        protected CouncilMessage(CouncilMessageTypes messageType) : base((ushort)messageType)
        {

        }

        protected CouncilMessage(CouncilMessageTypes messageType, ChainType chainType, int chainId, uint chainIndex, short issuer, Key issuerKey) : base((ushort)messageType)
        {
            ChainType = chainType;
            ChainId = chainId;
            ChainIndex = chainIndex;
            Issuer = issuer;

            _signKey = issuerKey;
        }

        protected override void Pack(Packer packer)
        {
            base.Pack(packer);

            packer.Pack((byte)ChainType);
            packer.Pack(ChainId);
            packer.Pack(ChainIndex);
            packer.Pack(Issuer);
        }

        protected override int PostSignaturePacked(Packer packer, int messageSize)
        {
            var startPosition = packer.Position - messageSize;
            packer.Position = startPosition;

            packer.AddSignature(_signKey, startPosition, messageSize);

            return Signature.GetSignatureBytes(Protocol.MessageKeyType);
        }

        protected override void Unpack(Unpacker unpacker)
        {
            base.Unpack(unpacker);

            ChainType = (ChainType)unpacker.UnpackLong();
            ChainId = unpacker.UnpackInt();
            ChainIndex = unpacker.UnpackUInt();
            Issuer = unpacker.UnpackShort();
        }

        protected override void PostSignatureUnpacked(Unpacker unpacker, int messageSize)
        {
            var startPosition = unpacker.Position - messageSize;

            (_dataHash, _signature) = unpacker.GetHashAndSignature(startPosition, messageSize);
        }

        public bool IsValidCouncilMemberSignature(Key memberKey)
        {
            return (memberKey != null && _dataHash != null && _signature != null && _signature.IsValid(memberKey, _dataHash));
        }
    }

    public static class CouncilMessageExtension
    {
        public static bool IsCouncilMessage(this Message message)
        {
            return (message.MessageType >= (ushort)CouncilMessageTypes.BlockProposal && message.MessageType < (ushort)CouncilMessageTypes.Last);
        }
    }
}
