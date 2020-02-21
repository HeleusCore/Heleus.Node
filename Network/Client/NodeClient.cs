using System;
using System.IO;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Chain;
using Heleus.Chain.Blocks;
using Heleus.Chain.Storage;
using Heleus.Cryptography;
using Heleus.Messages;

namespace Heleus.Network.Client
{
    public class NodeClient : ClientBase
    {
        public NodeClient(Uri endPoint) : base(endPoint, Protocol.CoreChainId)
        {
        }

        public async Task<NodeConnection> OpenNodeConnection()
        {
            var connection = new NodeConnection(new Uri(ChainEndPoint, "nodeconnection"));
            await connection.Connect();
            return connection;
        }

        public async Task<Download<LastBlockInfo>> DownloadLastBlockInfo(ChainType chainType, int chainId, uint chainIndex)
        {
            try
            {
                var data = await DownloadBinary($"dynamic/{GetChainTypeName(chainType)}/{chainId}/{chainIndex}/block/lastblockinfo/result.data");
                using (var unpacker = new Unpacker(data))
                {
                    var sliceInfo = new LastBlockInfo(unpacker);
                    return new Download<LastBlockInfo>(sliceInfo);
                }
            }
            catch (Exception exception)
            {
                return Download<LastBlockInfo>.HandleException(exception);
            }
        }

        public async Task<Download<SliceInfo>> DownloadTransactionStorageSliceInfo(ChainType chainType, int chainId, uint chainIndex)
        {
            try
            { 
                var data = await DownloadBinary($"dynamic/{GetChainTypeName(chainType)}/{chainId}/{chainIndex}/transaction/slice/info/result.data");
                using (var unpacker = new Unpacker(data))
                {
                    var sliceInfo = new SliceInfo(unpacker);
                    return new Download<SliceInfo>(sliceInfo);
                }
            }
            catch (Exception exception)
            {
                return Download<SliceInfo>.HandleException(exception);
            }
        }

        public async Task<Download<SliceInfo>> DownloadBlockStorageSliceInfo(ChainType chainType, int chainId, uint chainIndex)
        {
            try
            {
                var data = await DownloadBinary($"dynamic/{GetChainTypeName(chainType)}/{chainId}/{chainIndex}/block/slice/info/result.data");
                using (var unpacker = new Unpacker(data))
                {
                    var sliceInfo = new SliceInfo(unpacker);
                    return new Download<SliceInfo>(sliceInfo);
                }
            }
            catch (Exception exception)
            {
                return Download<SliceInfo>.HandleException(exception);
            }
        }

        public async Task<Download<KademliaQueryResultMessage>> DownloadKademliaNodes(Key targetNodeKey, Hash localId)
        {
            try
            {
                var data = await DownloadBinary($"dynamic/kademlia/nodes/{localId.HexString}/result.data");
                using (var unpacker = new Unpacker(data))
                {
                    var m = Message.Restore<KademliaQueryResultMessage>(unpacker);
                    if (m.IsValid(targetNodeKey))
                        return new Download<KademliaQueryResultMessage>(m);

                    return Download<KademliaQueryResultMessage>.InvalidSignature;
                }
            }
            catch (Exception exception)
            {
                return Download<KademliaQueryResultMessage>.HandleException(exception);
            }
        }

        public async Task<Download<BlockData>> DownloadBlockData(ChainType chainType, int chainId, uint chainIndex, long blockId)
        {
            try
            {
                var chainTypeName = chainType.ToString().ToLower();
                var blockRawData = await DownloadBinary($"static/{chainTypeName}chain/{chainId}/{chainIndex}/block/{blockId}/blockdata/result.data");

                return new Download<BlockData>(BlockData.Restore(blockRawData));
            }
            catch (Exception exception)
            {
                return Download<BlockData>.HandleException(exception);
            }
        }

        async Task<Download<SliceDownloadData>> DownloadSlice(Storage storage, string checksumUri, string headerUri, string dataUri, Func<FileStream, FileStream, FileStream, SliceDownloadData> callback)
        {
            var checksumStream = storage.FileTempStream();
            var headerStream = storage.FileTempStream();
            var dataStream = storage.FileTempStream();

            var delete = true;

            try
            {
                await DownloadToStream(checksumUri, checksumStream);
                await DownloadToStream(headerUri, headerStream);
                await DownloadToStream(dataUri, dataStream);

                checksumStream.Position = 0;
                var checksums = new ChecksumInfo(new Unpacker(checksumStream));
                checksumStream.Position = 0;
                headerStream.Position = 0;
                dataStream.Position = 0;

                var headerValid = checksums.Valid("header", headerStream);
                var dataValid = checksums.Valid("data", dataStream);

                if (!headerValid || !dataValid)
                    return Download<SliceDownloadData>.InvalidChecksum;

                delete = false;
                return new Download<SliceDownloadData>(callback.Invoke(checksumStream, headerStream, dataStream));
            }
            catch (Exception ex)
            {
                return Download<SliceDownloadData>.HandleException(ex);
            }
            finally
            {
                checksumStream.Dispose();
                headerStream.Dispose();
                dataStream.Dispose();

                if (delete)
                {
#pragma warning disable RECS0022 // A catch clause that catches System.Exception and has an empty body
                    try
                    {
                        File.Delete(checksumStream.Name);
                    }
                    catch { }

                    try
                    {
                        File.Delete(headerStream.Name);
                    }
                    catch { }

                    try
                    {
                        File.Delete(dataStream.Name);
                    }
                    catch { }

#pragma warning restore RECS0022 // A catch clause that catches System.Exception and has an empty body
                }
            }
        }

        public Task<Download<SliceDownloadData>> DownloadBlockSlice(Storage storage, ChainType chainType, int chainId, uint chainIndex, long sliceId)
        {
            var chainTypeName = chainType.ToString().ToLower();

            return DownloadSlice(storage,
                                 $"static/{chainTypeName}chain/{chainId}/{chainIndex}/transaction/slice/{sliceId}/data/result.data",
                                 $"static/{chainTypeName}chain/{chainId}/{chainIndex}/transaction/slice/{sliceId}/header/result.data",
                                 $"static/{chainTypeName}chain/{chainId}/{chainIndex}/transaction/slice/{sliceId}/checksums/result.data",
                                 (checksumStream, headerStream, dataStream) => new BlockSliceDownloadData(chainType, chainId, chainIndex, sliceId, storage, checksumStream, headerStream, dataStream));
        }

        public Task<Download<SliceDownloadData>> DownloadTransactionSlice(Storage storage, ChainType chainType, int chainId, uint chainIndex, long sliceId)
        {
            var chainTypeName = chainType.ToString().ToLower();

            return DownloadSlice(storage,
                                 $"static/{chainTypeName}chain/{chainId}/{chainIndex}/block/slice/{sliceId}/data/result.data",
                                 $"static/{chainTypeName}chain/{chainId}/{chainIndex}/block/slice/{sliceId}/header/result.data",
                                 $"static/{chainTypeName}chain/{chainId}/{chainIndex}/block/slice/{sliceId}/checksums/result.data",
                                 (checksumStream, headerStream, dataStream) => new TransactionSliceDownloadData(chainType, chainId, chainIndex, sliceId, storage, checksumStream, headerStream, dataStream));
        }
    }
}
