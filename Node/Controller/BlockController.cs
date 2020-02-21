using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace Heleus.Node.Controller
{
    public class BlockController : HeleusController
    {
        public BlockController(Node node) : base(node)
        {
        }

        [Route("static/{chainTypeName}chain/{chainid:int}/{chainindex:long}/block/{blockid:long}/blockdata/result.data")]
        public async Task<ActionResult> GetBlockData(string chainTypeName, int chainid, long chainindex, long blockid)
        {
            if(GetChainType(chainTypeName, out var chainType))
            {
                var chain = _node.ChainManager.GetChain(chainType, chainid, (uint)chainindex);
                if (chain != null)
                {
                    var blockData = await chain.BlockStorage.GetBlockData(blockid);
                    if(blockData != null)
                        return File(blockData.ToByteArray(), "application/octet-stream", "result.data");
                }
            }

            return NotFound();
        }

        [Route("static/{chainTypeName}chain/{chainid:int}/{chainindex:long}/block/lastblockinfo/result.data")]
        public ActionResult GetLastBlockInfo(string chainTypeName, int chainid, long chainindex)
        {
            if (GetChainType(chainTypeName, out var chainType))
            {
                var chain = _node.ChainManager.GetChain(chainType, chainid, (uint)chainindex);
                if (chain != null)
                {
                    return File(chain.BlockStorage.LastBlockInfo.ToByteArray(), "application/octet-stream", "result.data");
                }
            }

            return NotFound();
        }

        ActionResult GetBlockSliceStorage(string type, string chainTypeName, int chainid, uint chainIndex, long sliceid)
        {
            if (GetChainType(chainTypeName, out var chainType))
            {
                var chain = _node.ChainManager.GetChain(chainType, chainid, chainIndex);
                if (chain != null)
                {
                    if (chain.BlockStorage.IsBlockSliceAvailable(sliceid))
                        return PhysicalFile(Path.Combine(chain.BlockStorage.FullBlockStoragePath, $"{sliceid}.{type}"), "application/octet-stream", "result.data");
                }
            }

            return NotFound();
        }

        [Route("static/{chainTypeName}chain/{chainid:int}/{chainindex:long}/block/slice/{sliceid:long}/data/result.data")]
        public ActionResult GetBlockSliceData(string chainTypeName, int chainid, long chainindex, long sliceid)
        {
            return GetBlockSliceStorage("data", chainTypeName, chainid, (uint)chainindex, sliceid);
        }

        [Route("static/{chainTypeName}chain/{chainid:int}/{chainindex:long}/block/slice/{sliceid:long}/header/result.data")]
        public ActionResult GetBlockSlideHeader(string chainTypeName, int chainid, long chainindex, long sliceid)
        {
            return GetBlockSliceStorage("header", chainTypeName, chainid, (uint)chainindex, sliceid);
        }

        [Route("static/{chainTypeName}chain/{chainid:int}/{chainindex:long}/block/slice/{sliceid:long}/checksums/result.data")]
        public ActionResult GetBlockSliceChecksums(string chainTypeName, int chainid, long chainindex, long sliceid)
        {
            return GetBlockSliceStorage("checksums", chainTypeName, chainid, (uint)chainindex, sliceid);
        }

        [Route("dynamic/{chainTypeName}chain/{chainid:int}/{chainindex:long}/block/slice/info/result.data")]
        public ActionResult GetChainSliceInfoRaw(string chainTypeName, int chainid, long chainindex)
        {
            if (GetChainType(chainTypeName, out var chainType))
            {
                var chain = _node.ChainManager.GetChain(chainType, chainid, (uint)chainindex);
                if (chain != null)
                {
                    var blockSlice = chain.BlockStorage.GetStoredSliceInfo();

                    return File(blockSlice.ToByteArray(), "application/octet-stream", "result.data");
                }
            }
            return NotFound();
        }
    }
}
