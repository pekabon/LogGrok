using System;
using System.Collections.Concurrent;

namespace LogGrok.Unsafe
{
    public class SimpleObjectPool<T> 
    {
        private readonly Func<T> _createFunc;
        private readonly ConcurrentBag<T> _pool = new ConcurrentBag<T>();

        public SimpleObjectPool(Func<T> createFunc)
        {
            _createFunc = createFunc;
        }

        public T Get()
        {
            T result;
            if (!_pool.TryTake(out result))
                result = _createFunc();
            return result;
        }

        public void Release(T t)
        {
            _pool.Add(t);
        }
    }
}