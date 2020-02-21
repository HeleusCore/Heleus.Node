using System;
using Microsoft.AspNetCore.Mvc;

namespace Heleus.Node.Controller
{
    public class CoreAccountController : HeleusController
    {
        public CoreAccountController(Node node) : base(node)
        {
        }

        [Route("dynamic/corechain/account/{accountid:long}/result.data")]
        public ActionResult GetAccountRaw(long accountid)
        {
            var coreAccount = _node.ChainManager.CoreChain.GetCoreAccountData(accountid);
            if (coreAccount != null)
            {
                coreAccount.ZeroBalance();
                return File(coreAccount.ToByteArray(), "application/octet-stream", "result.data");
            }

            return this.NotFound();
        }
    }
}
