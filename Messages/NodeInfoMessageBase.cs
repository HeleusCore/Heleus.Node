using Heleus.Base;
using Heleus.Cryptography;
using Heleus.Network;
using Heleus.Node.Configuration;

namespace Heleus.Messages
{
    public class NodeInfoMessageBase : NodeMessage
    {
        public NodeInfo NodeInfo { get; private set; }
        public Hash ReceiverNodeId { get; private set; }

        public bool IsCoreNode => NodeInfo.CoreNodeKey != null;

        Signature _coreVoteSignature;
        Hash _coreVoteDataHash;
        readonly NodeConfiguration _nodeConfiguration;

        protected NodeInfoMessageBase(NodeMessageTypes nodeMessageType) : base(nodeMessageType)
        {

        }

        protected NodeInfoMessageBase(NodeMessageTypes nodeMessageType, NodeConfiguration nodeConfiguration, Hash receiverNodeId) : this(nodeMessageType)
        {
            NodeInfo = nodeConfiguration.LocaleNodeInfo;
            ReceiverNodeId = receiverNodeId;

            _nodeConfiguration = nodeConfiguration;
        }

        protected override void Pack(Packer packer)
        {
            base.Pack(packer);
            NodeInfo.Pack(packer);
            packer.Pack(ReceiverNodeId);
        }

        protected override int PostSignaturePacked(Packer packer, int messageSize)
        {
            if (IsCoreNode)
            {
                var startPosition = packer.Position - messageSize;
                packer.Position = startPosition;

                packer.AddSignature(_nodeConfiguration.CoreKey.DecryptedKey, startPosition, messageSize);

                return Signature.GetSignatureBytes(Protocol.MessageKeyType);
            }

            return base.PostSignaturePacked(packer, messageSize);
        }

        protected override void Unpack(Unpacker unpacker)
        {
            base.Unpack(unpacker);
            NodeInfo = new NodeInfo(unpacker);
            ReceiverNodeId = unpacker.UnpackHash();
        }

        protected override void PostSignatureUnpacked(Unpacker unpacker, int messageSize)
        {
            if (IsCoreNode)
            {
                var startPosition = unpacker.Position - messageSize;

                (_coreVoteDataHash, _coreVoteSignature) = unpacker.GetHashAndSignature(startPosition, messageSize);
            }
        }

        public bool IsCoreVoteSignatureValid(Key coreVoteKey)
        {
            return (coreVoteKey != null && _coreVoteDataHash != null && _coreVoteSignature != null && _coreVoteSignature.IsValid(coreVoteKey, _coreVoteDataHash));
        }
    }
}
