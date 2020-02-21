using System;
using System.IO;
using Microsoft.AspNetCore.Mvc;

namespace Heleus.Node.Controller
{
    public class TransactionController : HeleusController
    {
        public TransactionController(Node node) : base(node)
        {
        }

        [Route("static/{chainTypeName}chain/{chainid:int}/{chainindex:long}/transaction/item/{transactionid:long}/result.data")]
        public ActionResult GetTransactionItem(string chainTypeName, int chainid, long chainindex, long transactionid)
        {
            if(GetChainType(chainTypeName, out var chainType))
            {
                var chain = _node.ChainManager.GetChain(chainType, chainid, (uint)chainindex);
                if(chain != null)
                {
                    var transactionItemData = chain.TransactionStorage.GetTransactionItemData(transactionid);
                    if(transactionItemData != null)
                        return File(transactionItemData, "application/octet-stream", "result.data");
                }
            }

            return NotFound();
        }

        ActionResult GetTransactionSliceStorage(string type, string chainTypeName, int chainid, uint chainIndex, long sliceid)
        {
            if (GetChainType(chainTypeName, out var chainType))
            {
                var chain = _node.ChainManager.GetChain(chainType, chainid, chainIndex);
                if (chain != null)
                {
                    if (chain.TransactionStorage.IsTransactionSliceAvailable(sliceid))
                        return PhysicalFile(Path.Combine(chain.TransactionStorage.FullTransactionsStoragePath, $"{sliceid}.{type}"), "application/octet-stream", "result.data");
                }
            }

            return NotFound();
        }

        [Route("static/{chainTypeName}chain/{chainid:int}/{chainindex:long}/transaction/slice/{sliceid:long}/data/result.data")]
        public ActionResult GetTransactionSliceData(string chainTypeName, int chainid, long chainIndex, long sliceid)
        {
            return GetTransactionSliceStorage("data", chainTypeName, chainid, (uint)chainIndex, sliceid);
        }

        [Route("static/{chainTypeName}chain/{chainid:int}/{chainindex:long}/transaction/slice/{sliceid:long}/header/result.data")]
        public ActionResult GetTransactionSlideHeader(string chainTypeName, int chainid, long chainIndex, long sliceid)
        {
            return GetTransactionSliceStorage("header", chainTypeName, chainid, (uint)chainIndex, sliceid);
        }

        [Route("static/{chainTypeName}chain/{chainid:int}/{chainindex:long}/transaction/slice/{sliceid:long}/checksums/result.data")]
        public ActionResult GetTransactionSliceChecksums(string chainTypeName, int chainid, long chainIndex, long sliceid)
        {
            return GetTransactionSliceStorage("checksums", chainTypeName, chainid, (uint)chainIndex, sliceid);
        }

        [Route("dynamic/{chainTypeName}chain/{chainid:int}/{chainindex:long}/transaction/slice/info/result.data")]
        public ActionResult GetTransactionSliceInfo(string chainTypeName, int chainid, long chainIndex)
        {
            if (GetChainType(chainTypeName, out var chainType))
            {
                var chain = _node.ChainManager.GetChain(chainType, chainid, (uint)chainIndex);
                if (chain != null)
                {
                    var blockSlice = chain.TransactionStorage.GetStoredSliceInfo();

                    return File(blockSlice.ToByteArray(), "application/octet-stream", "result.data");
                }
            }
            return NotFound();
        }
    }
}
