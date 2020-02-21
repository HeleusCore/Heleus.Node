using System;
using System.IO;
using Heleus.Base;
using Heleus.Chain;
using Heleus.Chain.Storage;
using Heleus.Manager;

namespace Heleus.Network.Client
{
    public abstract class SliceDownloadData : IDisposable
    {
        public readonly string ChecksumFilePath;
        public readonly string HeaderFilePath;
        public readonly string DataFilePath;

        public readonly long SliceId;

        readonly Storage _storage;

        protected SliceDownloadData(long sliceId, Storage storage, FileStream checksumStream, FileStream headerStream, FileStream dataStream)
        {
            SliceId = sliceId;
            _storage = storage;

            ChecksumFilePath = checksumStream.Name;
            HeaderFilePath = headerStream.Name;
            DataFilePath = dataStream.Name;
        }

        protected abstract string GetPath();

        public void Delete()
        {
            var path = Path.Combine(_storage.Root.FullName, GetPath());

            var checksumPath = Path.Combine(path, SliceId + ".checksums");
            var headerPath = Path.Combine(path, SliceId + ".header");
            var dataPath = Path.Combine(path, SliceId + ".data");

            try
            {

            }
#pragma warning disable RECS0022 // A catch clause that catches System.Exception and has an empty body
            catch { }
            try
            {
                if (File.Exists(checksumPath))
                    File.Delete(checksumPath);
            }
            catch { }
            try
            {
                if (File.Exists(headerPath))
                    File.Delete(headerPath);
            }
            catch { }
            try
            {
                if (File.Exists(dataPath))
                    File.Delete(dataPath);
            }
            catch { }
#pragma warning restore RECS0022 // A catch clause that catches System.Exception and has an empty body
        }

        public bool Move()
        {
            var path = GetPath();
            if (!_storage.CreateDirectory(path))
                return false;

            path = Path.Combine(_storage.Root.FullName, path);

            var checksumPath = Path.Combine(path, SliceId + ".checksums");
            var headerPath = Path.Combine(path, SliceId + ".header");
            var dataPath = Path.Combine(path, SliceId + ".data");

            try
            {
                File.Delete(checksumPath);
                File.Delete(headerPath);
                File.Delete(dataPath);

                File.Move(HeaderFilePath, headerPath);
                File.Move(DataFilePath, dataPath);
                File.Move(ChecksumFilePath, checksumPath);
            }
            catch (Exception ex)
            {
                Log.IgnoreException(ex);
#pragma warning disable RECS0022 // A catch clause that catches System.Exception and has an empty body
                try
                {
                    File.Delete(checksumPath);
                }
                catch { }

                try
                {
                    File.Delete(headerPath);
                }
                catch { }

                try
                {
                    File.Delete(dataPath);
                }
                catch { }
#pragma warning restore RECS0022 // A catch clause that catches System.Exception and has an empty body
                return false;
            }

            return true;
        }

        ~SliceDownloadData()
        {
            Dispose();
        }

        public void Dispose()
        {
#pragma warning disable RECS0022 // A catch clause that catches System.Exception and has an empty body
            try
            {
                File.Delete(ChecksumFilePath);
            }
            catch { }

            try
            {
                File.Delete(HeaderFilePath);
            }
            catch { }

            try
            {
                File.Delete(DataFilePath);
            }
            catch { }
#pragma warning restore RECS0022 // A catch clause that catches System.Exception and has an empty body

            GC.SuppressFinalize(this);
        }
    }

    public class TransactionSliceDownloadData : SliceDownloadData
    {
        public readonly ChainType ChainType;
        readonly int _chainId;
        readonly uint _chainIndex;

        public TransactionSliceDownloadData(ChainType chainType, int chainId, uint chainIndex, long sliceId, Storage storage, FileStream checksumStream, FileStream headerStream, FileStream dataStream) : base(sliceId, storage, checksumStream, headerStream, dataStream)
        {
            ChainType = chainType;
            _chainId = chainId;
            _chainIndex = chainIndex;
        }

        protected override string GetPath()
        {
            return TransactionStorage.GetTransactionStoragePath(ChainType, _chainId, _chainIndex); ;
        }
    }

    public class BlockSliceDownloadData : SliceDownloadData
    {
        public readonly ChainType ChainType;
        readonly int _chainId;
        readonly uint _chainIndex;

        public BlockSliceDownloadData(ChainType chainType, int chainId, uint chainIndex, long sliceId, Storage storage, FileStream checksumStream, FileStream headerStream, FileStream dataStream) : base(sliceId, storage, checksumStream, headerStream, dataStream)
        {
            ChainType = chainType;
            _chainId = chainId;
            _chainIndex = chainIndex;
        }

        protected override string GetPath()
        {
            return BlockStorage.GetBlockStoragePath(ChainType, _chainId, _chainIndex);
        }
    }
}
