using System;
using System.Collections.Generic;
using Heleus.Cryptography;
using Heleus.Messages;
using Heleus.Network;
using Heleus.Node.Views;
using Microsoft.AspNetCore.Mvc;

namespace Heleus.Node.Controller
{
    public class KademliaController : HeleusController

    {
        public KademliaController(Node node) : base(node)
        {
        }

        List<NodeInfo> GetNodes(string nodeid)
        {
            if (string.IsNullOrEmpty(nodeid) || nodeid.Length != 46)
                return null;
            
            var id = Hash.Restore(nodeid);
            if (id != null && id.HashType == HashTypes.Sha1)
            {
                return _node.Kademlia.GetNearestNodes(id);
            }
            return null;
        }

        [Route("dynamic/kademlia/nodes/{nodeid}/result.data")]
        public ActionResult GetNodesRaw(string nodeid)
        {
            var nodes = GetNodes(nodeid);
            if (nodes != null)
            {
				var key = _node.NodeConfiguration.LocaleNodePrivateKey;
                var m = new KademliaQueryResultMessage
                {
                    Nodes = nodes,
                    SignKey = key
                };

                return File(m.ToByteArray(), "application/octet-stream", "result.data");
            }
            return BadRequestJson();
        }
    }
}
