using System;
using Heleus.Node.Views;
using Microsoft.AspNetCore.Mvc;

namespace Heleus.Node.Controller
{
    public class NodeController : HeleusController

    {
        public NodeController(Node node) : base(node)
        {
        }

        [Route("static/node/nodeinfo/result.data")]
        public ActionResult GetNodeInfoRaw()
        {
			return File(_node.NodeConfiguration.LocaleNodeInfo.NodeInfoData, "application/octet-stream", "result.data");
        }
    }
}
