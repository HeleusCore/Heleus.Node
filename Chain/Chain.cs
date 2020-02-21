using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Chain.Storage;
using Heleus.Network;

namespace Heleus.Chain
{
    public abstract class Chain : ILogger
    {
        public string LogName => GetType().Name;

        public readonly ChainType ChainType;
        public readonly int ChainId;
        public readonly uint ChainIndex;

        public long LastProcessedBlockId
        {
            get => _metaDiscStorage[0].LastBlockId;
        }

        public long LastProcessedTransactionId
        {
            get => _metaDiscStorage[0].LastTransactionId;
        }

        protected readonly List<MetaDiscStorage> _metaDiscStorage = new List<MetaDiscStorage>();

        bool _active;

        public bool Active
        {
            get
            {
                lock (_lock)
                    return _active;
            }
        }

        public class AvailableEndPoint : IEquatable<AvailableEndPoint>
        {
            public readonly Uri EndPoint;

            NodeInfo _nodeInfo;
            public NodeInfo NodeInfo
            {
                get
                {
                    lock (this)
                        return _nodeInfo;
                }

                set
                {
                    lock (this)
                        _nodeInfo = value;
                }
            }

            public AvailableEndPoint(Uri endPoint)
            {
                EndPoint = endPoint;
            }

            public override int GetHashCode()
            {
                return EndPoint.GetHashCode();
            }

            public bool Equals(AvailableEndPoint other)
            {
                return EndPoint.Equals(other?.EndPoint);
            }

            public override bool Equals(object obj)
            {
                return Equals(obj as AvailableEndPoint);
            }
        }

        public IReadOnlyList<AvailableEndPoint> AvailableEndpoints { get; protected set; } = new List<AvailableEndPoint>();

        protected bool IsValidAvailableEndPoint(Uri endPoint)
        {
            return !(_node.Host.LocalEndpoint == endPoint || _node.Host.PublicEndPoint == endPoint);
        }

        public BlockStorage BlockStorage { get; private set; }
        public TransactionStorage TransactionStorage { get; protected set; }

        protected readonly Node.Node _node;
        protected readonly Base.Storage _storage;

        protected readonly object _lock = new object();

        public static string GetChainMetaDirectory(ChainType chainType, int chainId, uint chainIndex)
        {
            return $"chains/{chainType.ToString().ToLower()}/{chainId}_{chainIndex}/meta/";
        }

        public static void CreateRequiredDirectories(Base.Storage storage, ChainType chainType, int chainId, uint chainIndex, bool createStorageDirectories)
        {
            if (!storage.CreateDirectory(GetChainMetaDirectory(chainType, chainId, chainIndex)))
                throw new Exception("Could not create chain meta directory.");

            if (createStorageDirectories)
            {
                BlockStorage.CreateRequiredDirectories(storage, chainType, chainId, chainIndex);
                TransactionStorage.CreateRequiredDirectories(storage, chainType, chainId, chainIndex);
            }
        }

#pragma warning disable RECS0021 // Warns about calls to virtual member functions occuring in the constructor
        protected Chain(ChainType chainType, int chainId, uint chainIndex, Node.Node node)
        {
            _node = node;
            _storage = node.Storage;

            ChainType = chainType;
            ChainId = chainId;
            ChainIndex = chainIndex;

            BlockStorage = new BlockStorage(chainType, chainId, chainIndex, node);

            CreateRequiredDirectories(_storage, chainType, chainId, chainIndex, false);
        }
#pragma warning restore RECS0021

        void DeleteMetaDiscStorages()
        {
            try
            {
                foreach (var item in _metaDiscStorage)
                    item.Delete();
            }
            catch { }
            _metaDiscStorage.Clear();

            _storage.DeleteDirectory(GetChainMetaDirectory(ChainType, ChainId, ChainIndex));
            CreateRequiredDirectories(_storage, ChainType, ChainId, ChainIndex, true);

            NewMetaStorage();
        }

        public virtual Task Initalize()
        {
            if(!NewMetaStorage())
            {
                DeleteMetaDiscStorages();
            }

            for (var i = 1; i < _metaDiscStorage.Count; i++)
            {
                if (_metaDiscStorage[i - 1].LastBlockId != _metaDiscStorage[i].LastBlockId)
                {
                    Log.Warn($"Internal storage failure. Rebuilding.", this);

                    DeleteMetaDiscStorages();
                    break;
                }
            }

            return Task.CompletedTask;
        }

        abstract protected bool NewMetaStorage();
        abstract protected void ClearMetaData();

        public virtual async Task Start()
        {
            await Stop();
            await BlockStorage.Start();

            lock (_lock)
            {
                TransactionStorage = new TransactionStorage(_storage, ChainType, ChainId, ChainIndex);

                try
                {
                    BuildMetaData();
                }
                catch(Exception ex)
                {
                    Log.IgnoreException(ex, this);

                    DeleteMetaDiscStorages();
                    BuildMetaData();
                }

                _active = true;
            }
        }

        public virtual Task Stop()
        {
            ClearMetaData();
            lock (_lock)
            {
                if (TransactionStorage != null)
                {
                    TransactionStorage.Dispose();
                    TransactionStorage = null;
                }

                _active = false;
            }

            BlockStorage.Stop();

            return Task.CompletedTask;
        }

        protected abstract void BuildMetaData(TransactionDiscStorage transactionStorage, long sliceIndex);

        void BuildMetaData()
        {
            var startSliceId = TransactionStorage.GetSliceIdForTransactionId(LastProcessedTransactionId);
            if (startSliceId >= 0)
            {
                var endSliceId = TransactionStorage.CurrentSliceId;

                for (var sliceid = startSliceId; sliceid <= endSliceId; sliceid++)
                {
                    using (var transactionStorage = TransactionStorage.GetTransactionStorage(sliceid))
                    {
                        if (transactionStorage.Length > 0)
                        {
                            BuildMetaData(transactionStorage, sliceid);

                            // prevent overflow for large syncs
                            ClearMetaData();

                            foreach (var metaStorage in _metaDiscStorage)
                            {
                                metaStorage.LastBlockId = Math.Max(metaStorage.LastBlockId, transactionStorage.LastBlockId);
                                metaStorage.LastTransactionId = Math.Max(metaStorage.LastTransactionId, transactionStorage.EndIndex);
                                metaStorage.Commit();
                            }
                        }
                    }
                }
            }
        }
    }
}
