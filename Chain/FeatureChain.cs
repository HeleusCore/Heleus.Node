using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Chain.Service;
using Heleus.Chain.Storage;
using Heleus.Service;
using Heleus.Transactions;
using Heleus.Transactions.Features;

namespace Heleus.Chain
{
    public abstract class FeatureChain : Chain, IFeatureChain, IMetaStorageRegistrar
    {
        ChainType IFeatureChain.ChainType => ChainType;
        int IFeatureChain.ChainId => ChainId;
        uint IFeatureChain.ChainIndex => ChainIndex;
        IFeatureHost IFeatureChain.FeatureHost => ServiceHost;

        readonly Dictionary<ushort, FeatureChainHandler> _chainHandlers = new Dictionary<ushort, FeatureChainHandler>();
        readonly Dictionary<ushort, FeatureQueryHandler> _queryHandlers = new Dictionary<ushort, FeatureQueryHandler>();

        class MetaStorageInfo : IMetaStorage
        {
            public readonly Feature Feature;
            public readonly string Name;
            public readonly int BlockSize;
            public readonly DiscStorageFlags StorageFlags;
            public DiscStorage Storage { get; internal set; }

            public MetaStorageInfo(Feature feature, string name, int blockSize, DiscStorageFlags storageFlags)
            {
                Feature = feature;
                Name = name;
                BlockSize = blockSize;
                StorageFlags = storageFlags;
            }
        }

        readonly List<MetaStorageInfo> _storagesInfo = new List<MetaStorageInfo>();

        protected HashSet<ushort> _enabledFeatures;
        protected HashSet<ushort> _forceFeatures;

        readonly BlockTransactionGenerator _featureChecker;
        ServiceOptions _serviceOptions;

        public ServiceHost ServiceHost { get; private set; }

        public FeatureChain(ChainType chainType, int chainId, uint chainIndex, Node.Node node) : base(chainType, chainId, chainIndex, node)
        {
            _featureChecker = new BlockTransactionGenerator(this);
        }

        public override Task Initalize()
        {
            ServiceHost = _node.ChainManager.GetServiceHost(ChainId);
            _serviceOptions = ServiceHost.ServiceOptions;

            _enabledFeatures = new HashSet<ushort>(_serviceOptions.GetChainFeatures(ChainType, ChainIndex));
            _forceFeatures = new HashSet<ushort>(_serviceOptions.GetForcedChainFeatures(ChainType, ChainIndex));

            foreach (var featureId in _enabledFeatures)
            {
                var feature = Feature.GetFeature(featureId);
                if (feature == null)
                {
                    Log.Error($"Unkown feature {featureId}", this);
                }
                else
                {
                    foreach(var required in feature.RequiredFeatures)
                    {
                        if(!_enabledFeatures.Contains(required))
                        {
                            var feat = Feature.GetFeature(required);
                            Log.Error($"Feature {feature.GetType().Name} ({featureId}) requires feature {feat?.GetType()?.Name} ({required}) to work.", this);
                        }
                    }

                    if (feature.RequiresChainHandler)
                    {
                        var chainHandler = feature.NewChainHandler(this);
                        if (chainHandler == null)
                        {
                            Log.Error($"Invalid feature {feature.GetType().Name}, ChainHandler is missing.", this);
                        }
                        else
                        {
                            chainHandler.RegisterMetaStorages(this);
                            _chainHandlers[featureId] = chainHandler;
                        }
                    }
#if DEBUG
                    else
                    {
                        try
                        {
                            if (feature.NewChainHandler(this) != null)
                                Log.Error($"NewFeatureChainHandler is not null for {feature.GetType().Name}.", this);
                        } catch { }
                    }
#endif

                    if (feature.RequiresQueryHandler)
                    {
                        var queryHandler = feature.NewQueryHandler(this);
                        if (queryHandler == null)
                        {
                            Log.Error($"Invalid feature {feature.GetType().Name}, ChainHandler is missing.", this);
                        }
                        else
                        {
                            _queryHandlers[featureId] = queryHandler;
                        }
                    }
#if DEBUG
                    else
                    {
                        try
                        {
                            if (feature.NewQueryHandler(this) != null)
                                Log.Error($"NewQueryHandler is not null for {feature.GetType().Name}.", this);
                        } catch { }
                    }
#endif
                }
            }

            return base.Initalize();
        }

        protected CommitItems NewCommitItems()
        {
            return new CommitItems(_chainHandlers);
        }

        public FeatureChainHandler GetFeatureChainHandler(ushort featureId)
        {
            _chainHandlers.TryGetValue(featureId, out var handler);
            return handler;
        }

        public T GetFeatureChainHandler<T>(ushort featureId) where T : FeatureChainHandler
        {
            return GetFeatureChainHandler(featureId) as T;
        }

        public IMetaStorage AddMetaStorage(Feature feature, string name, int blockSize, DiscStorageFlags storageFlags)
        {
            var info = new MetaStorageInfo(feature, name, blockSize, storageFlags);
            _storagesInfo.Add(info);
            return info;
        }

        public FeatureQueryHandler GetFeatureQueryHandler(ushort featureId)
        {
            _queryHandlers.TryGetValue(featureId, out var handler);
            return handler;
        }

        public int GetIntOption(ushort featureId, int option, int defaultValue)
        {
            return _serviceOptions.GetIntOption(ChainType, ChainIndex, featureId, option, defaultValue);
        }

        public long GetLongOption(ushort featureId, int option, long defaultValue)
        {
            return _serviceOptions.GetLongOption(ChainType, ChainIndex, featureId, option, defaultValue);
        }

        protected override void ClearMetaData()
        {
            foreach (var handler in _chainHandlers.Values)
                handler.ClearMetaData();
        }

        protected override bool NewMetaStorage()
        {
            try
            {
                foreach (var info in _storagesInfo)
                {
                    var storage = new MetaDiscStorage(_storage, ChainType, ChainId, ChainIndex, $"feature_{info.Feature.FeatureId}_{info.Name}", info.BlockSize, info.StorageFlags);

                    _metaDiscStorage.Add(storage);
                    info.Storage = storage;
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.HandleException(ex, this);
            }

            return false;
        }

        protected void ConsumeTransactionFeatures(ushort featureRequestTransactionType, Transaction transaction, FeatureAccount featureAccount, CommitItems commitItems)
        {
            var transactionType = transaction.OperationType;
            if (transactionType == featureRequestTransactionType)
            {
                var featAtt = transaction as FeatureRequestDataTransaction;
                var (handler, commit) = commitItems.Get(featAtt.FeatureId);

                if (handler != null)
                {
                    try
                    {
                        handler.ConsumeFeatureRequest(commitItems, commit, featAtt.Request, transaction);
                    }
                    catch(Exception ex)
                    {
                        Log.HandleException(ex, this);
                    }
                }
                else
                {
                    Log.Warn($"FeatureChainHanler {featAtt.FeatureId} not found, couldn't consume request {featAtt?.Request?.GetType()?.Name}");
                }
            }

            foreach (var featureData in transaction.Features)
            {
                try
                {
                    if (featureData.Feature.HasAccountContainer)
                    {
                        var container = featureAccount.GetOrAddFeatureContainer(featureData.FeatureId);
                        container.Update(commitItems, this, transaction, featureData);
                    }
#if DEBUG
                    else
                    {
                        try
                        {
                            var container = featureAccount.GetOrAddFeatureContainer(featureData.FeatureId);
                            if (container != null)
                            {
                                Log.Warn($"FeatureContainer not null for {featureData.Feature.GetType().Name}", this);
                            }
                        } catch { }
                    }
#endif
                    var (handler, commit) = commitItems.Get(featureData.FeatureId);
                    if (handler != null)
                        handler.ConsumeTransactionFeature(commitItems, commit, transaction, featureAccount, featureData);
                }
                catch (Exception ex)
                {
                    Log.HandleException(ex, this);
                }
            }
        }

        protected (TransactionResultTypes, long) ValidateTransactionFeatures(ushort featureRequestTransactionType, Transaction transaction)
        {
            long userCode = 0;
            TransactionResultTypes result;

            if (transaction.UnkownFeatureIds.Count > 0)
            {
                result = TransactionResultTypes.FeatureUnknown;
                userCode = transaction.UnkownFeatureIds.First();
                goto end;
            }

            if (_forceFeatures.Count > 0)
            {
                foreach (var forcedFeatureId in _forceFeatures)
                {
                    if (!transaction.HasFeature(forcedFeatureId))
                    {
                        userCode = Feature.SetFeatureCode(forcedFeatureId, 0);
                        result = TransactionResultTypes.FeatureMissing;
                        goto end;
                    }
                }
            }

            foreach (var featureId in transaction.FeatureIds)
            {
                if (!_enabledFeatures.Contains(featureId))
                {
                    userCode = Feature.SetFeatureCode(featureId, 0);
                    result = TransactionResultTypes.FeatureNotAvailable;
                    goto end;
                }
            }

            try
            {
                var (checkResult, checkFatureId, checkUserCode) = _featureChecker.AddTransaction(BlockTransactionGeneratorMode.Validate, this, transaction, null);
                if (checkResult != TransactionResultTypes.Ok)
                {
                    userCode = Feature.SetFeatureCode(checkFatureId, checkUserCode);
                    result = checkResult;
                    goto end;
                }
            }
            catch(Exception ex)
            {
                Log.HandleException(ex, this);
                result = TransactionResultTypes.FeatureInternalError;
                goto end;
            }

            var transactionType = transaction.OperationType;
            if (transactionType == featureRequestTransactionType && transaction is IFeatureRequestTransaction featAtt)
            {
                if (!featAtt.IsRequestValid)
                {
                    userCode = Feature.SetFeatureCode(featAtt.FeatureId, 4);
                    result = TransactionResultTypes.FeatureInternalError;
                    goto end;
                }

                var handler = GetFeatureChainHandler(featAtt.FeatureId);

                if (handler != null)
                {
                    try
                    { 
                        var (valid, code) = handler.ValidateFeatureRequest(featAtt.Request, transaction);
                        if (!valid)
                        {
                            userCode = Feature.SetFeatureCode(featAtt.FeatureId, code);
                            result = TransactionResultTypes.FeatureCustomError;
                            goto end;
                        }
                    }
                    catch
                    {
                        result = TransactionResultTypes.FeatureInternalError;
                        goto end;
                    }
                }
                else
                {
                    userCode = Feature.SetFeatureCode(featAtt.FeatureId, 4);
                    result = TransactionResultTypes.FeatureInternalError;
                    goto end;
                }
            }

            result = TransactionResultTypes.Ok;

        end:

            return (result, userCode);
        }

        public abstract FeatureAccount GetFeatureAccount(long accountId);
        public abstract bool FeatureAccountExists(long accountId);
    }
}
