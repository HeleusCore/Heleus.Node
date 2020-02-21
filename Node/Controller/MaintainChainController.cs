using System;
using Heleus.Chain.Maintain;
using Heleus.Network.Results;
using Microsoft.AspNetCore.Mvc;

namespace Heleus.Node.Controller
{
    public class MaintainChainController : HeleusController
    {
        public MaintainChainController(Node node) : base(node)
        {
        }

        [Route("dynamic/maintainchain/{chainid:int}/revenue/{accountid:long}/result.data")]
        public ActionResult GetServiceInfo(int chainid, long accountid)
        {
            Result result = null;

            var maintainChain = _node.ChainManager.GetMaintainChain(chainid);
            if (maintainChain != null)
            {
                if(maintainChain.GetAccountRevenueInfo(accountid, out var accountRevenueInfo))
                { 
                    result = new PackableResult<AccountRevenueInfo>(accountRevenueInfo);
                }
                else
                {
                    result = Result.AccountNotFound;
                }
            }
            else
            {
                result = Result.ChainNotFound;
            }

            return File(result.ToByteArray(), "application/octet-stream", "result.data");
        }
    }
}
