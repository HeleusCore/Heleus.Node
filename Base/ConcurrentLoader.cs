using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Heleus.Base
{
    public class ConcurrentLoader<TKey, TType>
    {
        readonly SemaphoreSlim _sem = new SemaphoreSlim(1);
        readonly Func<TKey, Task<TType>> _query;
        readonly Func<TKey, Task<TType>> _store;

        public ConcurrentLoader(Func<TKey, Task<TType>> query, Func<TKey, Task<TType>> store)
        {
            _query = query;
            _store = store;
        }

        public async Task<TType> Get(TKey key)
        {
            var data = await _query.Invoke(key);
            if (!data.IsNullOrDefault())
                return data;

            await _sem.WaitAsync();
            try
            {
                data = await _query.Invoke(key);
                if (!data.IsNullOrDefault())
                    return data;

                data = await _store.Invoke(key);
            }
            catch(Exception ex)
            {
                Log.IgnoreException(ex);
            }

            _sem.Release();
            return data;
        }
    }
}
