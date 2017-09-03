using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace LogGrok.Unsafe
{
    public class SimpleObjectPool<T> 
    {
        private readonly Func<T> _createFunc;
        private readonly object _lock;
        private readonly Stack<T> _pool = new Stack<T>();


        public SimpleObjectPool(Func<T> createFunc)
        {
            _createFunc = createFunc;
            _lock = new Object();
        }

        public T Get()
        {
            lock (_lock)
            {
                if (_pool.Count > 0)
                    return _pool.Pop();
            }
            return _createFunc();
        }

        public void Release(T t)
        {
            lock (_lock)
            {
                _pool.Push(t);
            }
        }


    }
}