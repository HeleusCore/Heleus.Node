using System;
using Microsoft.AspNetCore.Mvc;

namespace Heleus.Node.Controller
{
    public class CoreChainController : HeleusController

    {
        public CoreChainController(Node node) : base(node)
        {
        }

        [Route("dynamic/corechain/chaininfo/{chainid:int}/result.data")]
        public ActionResult GetChainInfoRaw(int chainid)
        {
            var chainInfo = _node.ChainManager.CoreChain.GetChainInfoData(chainid);
            if (chainInfo != null)
                return File(chainInfo.ToByteArray(), "application/octet-stream", "result.data");

            return this.NotFound();
        }
    }
}
