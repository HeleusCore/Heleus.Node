using Heleus.Chain;
using Heleus.Chain.Data;
using Heleus.Network.Results;
using Heleus.Transactions.Features;
using Microsoft.AspNetCore.Mvc;

namespace Heleus.Node.Controller
{
    public class FeatureController : HeleusController
    {
        public FeatureController(Node node) : base(node)
        {
        }

        [Route("dynamic/{chaintypename}chain/{chainid:int}/{chainindex:long}/feature/{featureid:int}/{*path}")]
        public ActionResult GetDynamicFeature(string chaintypename, int chainid, long chainindex, int featureid, string path)
        {
            Result result;
            if (GetChainType(chaintypename, out var chainType))
            {
                if (_node.ChainManager.GetChain(chainType, chainid, (uint)chainindex) is FeatureChain featureChain)
                {
                    var queryHandler = featureChain.GetFeatureQueryHandler((ushort)featureid);
                    if (queryHandler != null)
                    {
                        var query = FeatureQuery.Parse(queryHandler.FeatureId, path);
                        if (query != null)
                        {
                            result = queryHandler.QueryFeature(query);
                        }
                        else
                        {
                            result = Result.InvalidQuery;
                        }
                    }
                    else
                    {
                        result = Result.FeatureNotFound;
                    }
                }
                else
                {
                    result = Result.ChainNotFound;
                }
            }
            else
            {
                result = Result.InvalidQuery;
            }

            return File(result.ToByteArray(), "application/octet-stream", "result.data");
        }
    }
}
