using System;

namespace LogGrok.Unsafe
{
    public sealed class CrlfSearcher : CrLfSearcherBase<CrlfSearcher.Result>
    {
        public class Result : IDisposable
        {
            private readonly SimpleObjectPool<int[]> _intArrayPool;

            internal Result(RawLineReader.BufferReadResult rawLineReaderResult, SimpleObjectPool<int[]> intArrayPool)
            {
                _intArrayPool = intArrayPool;
                _rawLineReaderResult = rawLineReaderResult;
                ByteIndices = _intArrayPool.Get();
                ResultCount = 0;
            }

            private readonly RawLineReader.BufferReadResult _rawLineReaderResult;

            public byte[] Buffer => _rawLineReaderResult.Buffer;
            public long Position => _rawLineReaderResult.Position;

            public int[] ByteIndices { get; internal set; }
            public int ResultCount { get; internal set; }

            public void Dispose()
            {
                _intArrayPool.Release(ByteIndices);
                _rawLineReaderResult.Dispose();
            }
        }

        private readonly SimpleObjectPool<int[]> _intArrayPool = new SimpleObjectPool<int[]>(() => new int[16384]);

        public CrlfSearcher(byte[] r, byte[] n, int charSize) : base(r, n, charSize)
        {
        }

        internal Result ParseBuffer(RawLineReader.BufferReadResult rawLineReaderResult)
        {
            var result = new Result(rawLineReaderResult, _intArrayPool);

            ProcessBuffer(result.Buffer, 0, result.Buffer.Length, result);
            return result;
        }

        protected override void ProcessLine(int start, int len, Result context)
        {
            var index = context.ResultCount * 2;
            if (index + 2 >= context.ByteIndices.Length)
                IncreaseBuffer(context);

            var byteIndices = context.ByteIndices;
            byteIndices[index] = start;
            byteIndices[index + 1] = len;

            context.ResultCount++;
        }

        private static void IncreaseBuffer(Result context)
        {
            var newByteIndices = new int[2 * context.ByteIndices.Length];
            Array.Copy(context.ByteIndices, 0, newByteIndices, 0, context.ResultCount * 2);
            context.ByteIndices = newByteIndices;
        }
    }
}