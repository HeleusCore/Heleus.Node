using System;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Network.Results;
using Heleus.Service;
using Microsoft.AspNetCore.Mvc;

namespace Heleus.Node.Controller
{
    public class ServiceChainController : HeleusController
    {
        public ServiceChainController(Node node) : base(node)
        {
        }

        [Route("dynamic/servicechain/{chainid:int}/service/info/result.data")]
        public ActionResult GetServiceInfo(int chainid)
        {
            Result result = null;

            var chainContainer = _node.ChainManager.GetContainer(chainid);
            if (chainContainer != null)
            {
                var serviceInfo = chainContainer?.ServiceHost?.ServiceInfo;
                if (serviceInfo != null)
                {
                    result = new PackableResult<ServiceInfo>(serviceInfo);
                }
                else
                {
                    result = Result.DataNotFound;
                }
            }
            else
            {
                result = Result.ChainNotFound;
            }

            return File(result.ToByteArray(), "application/octet-stream", "result.data");
        }

        [Route("static/servicechain/{chainid}/service/querydata/{*path}")]
        public async Task<ActionResult> GetServiceDataStatic(int chainid, string path)
        {
            Result result = null;
            var serviceHost = _node.ChainManager.GetServiceHost(chainid);
            if (serviceHost != null)
            {
                var uriProvider = serviceHost.UriHandler;
                if (uriProvider != null && !string.IsNullOrEmpty(path))
                {
                    var r = await uriProvider.QueryStaticUriData(path);
                    if (r != null)
                    {
                        result = new PackableResult<IPackable>(r);
                    }
                }

                if (result == null)
                    result = Result.DataNotFound;
            }
            else
            {
                result = Result.ChainNotFound;
            }

            return File(result.ToByteArray(), "application/octet-stream", "result.data");
        }

        [Route("dynamic/servicechain/{chainid}/service/querydata/{*path}")]
        public async Task<ActionResult> GetChainServiceDynamicHandlerRaw(int chainid, string path)
        {
            Result result = null;
            var serviceHost = _node.ChainManager.GetServiceHost(chainid);
            if (serviceHost != null)
            {
                var uriProvider = serviceHost?.UriHandler;
                if (uriProvider != null && !string.IsNullOrEmpty(path))
                {
                    var r = await uriProvider.QueryDynamicUriData(path);
                    if (r != null)
                    {
                        result = new PackableResult<IPackable>(r);
                    }
                }

                if (result == null)
                    result = Result.DataNotFound;
            }
            else
            {
                result = Result.ChainNotFound;
            }

            return File(result.ToByteArray(), "application/octet-stream", "result.data");
        }
    }
}
