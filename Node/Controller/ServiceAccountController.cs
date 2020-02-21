using System;
using Heleus.Base;
using Heleus.Chain;
using Heleus.Cryptography;
using Heleus.Network.Results;
using Microsoft.AspNetCore.Mvc;

namespace Heleus.Node.Controller
{
    public class ServiceAccountController : HeleusController
    {
        public ServiceAccountController(Node node) : base(node)
        {
        }

        [Route("static/servicechain/{chainid:int}/account/{accountid:long}/key/{keyindex:int}/valid/result.data")] // keyindex:short throws asp exception
        public ActionResult GetValidServiceAccountKey(int chainid, long accountid, int keyindex)
        {
            Result result;

            var chain = _node.ChainManager.GetServiceChain(chainid);
            if (chain != null)
            {
                var key = chain.GetValidServiceAccountKey(accountid, (short)keyindex, Time.Timestamp);
                if (key != null)
                    result = new PackableResult<PublicServiceAccountKey>(key);
                else
                    result = Result.AccountNotFound;
            }
            else
            {
                result = Result.ChainNotFound;
            }

            return File(result.ToByteArray(), "application/octet-stream", "result.data");
        }

        [Route("static/servicechain/{chainid:int}/account/{accountid:long}/key/{publicKey}/revokeable/frompublickey/result.data")]
        public ActionResult GetRevokeableServiceAccountKey(int chainid, long accountid, string publicKey)
        {
            Result result;

            var chain = _node.ChainManager.GetServiceChain(chainid);
            if (chain != null)
            {
                var pk = Key.Restore(publicKey);
                if(chain.GetRevokealbeServiceAccountKey(accountid, pk, out var isValidAccount, out var revokeableKey))
                {
                    result = new PackableResult<RevokeablePublicServiceAccountKey>(revokeableKey);
                }
                else
                {
                    if (!isValidAccount)
                        result = Result.AccountNotFound;
                    else
                        result = Result.DataNotFound;
                }
            }
            else
            {
                result = Result.ChainNotFound;
            }

            return File(result.ToByteArray(), "application/octet-stream", "result.data");
        }

        [Route("dynamic/servicechain/{chainid:int}/account/{accountid:long}/key/{keyindex:int}/revokeable/result.data")] // keyindex:short throws asp exception
        public ActionResult GetRevokeableServiceAccountKey(int chainid, long accountid, int keyindex)
        {
            Result result;

            var chain = _node.ChainManager.GetServiceChain(chainid);
            if (chain != null)
            {
                var key = chain.GetRevokealbeServiceAccountKey(accountid, (short)keyindex);
                if (key != null)
                    result = new PackableResult<RevokeablePublicServiceAccountKey>(key);
                else
                    result = Result.AccountNotFound;
            }
            else
            {
                result = Result.ChainNotFound;
            }

            return File(result.ToByteArray(), "application/octet-stream", "result.data");
        }

        [Route("dynamic/servicechain/{chainid:int}/account/{accountid:long}/key/nextkeyindex/result.data")]
        public ActionResult GetNextServiceKeyIndex(int chainid, long accountid)
        {
            Result result = null;

            var chain = _node.ChainManager.GetServiceChain(chainid);
            if (chain != null)
            {
                if(chain.GetNextServiceAccountKeyIndex(accountid, out var nextKeyIndex))
                    result = new NextServiceAccountKeyIndexResult(nextKeyIndex);
                else
                    result = Result.AccountNotFound;
            }
            else
            {
                result = Result.ChainNotFound;
            }

            return File(result.ToByteArray(), "application/octet-stream", "result.data");
        }
    }
}
