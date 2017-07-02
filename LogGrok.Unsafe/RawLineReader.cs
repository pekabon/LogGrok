using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Nemerle.Core;

namespace LogGrok.Unsafe
{
    public class RawLineReader
    {
        private readonly byte[] _rBytes;
        private readonly byte[] _nBytes;
        private readonly int _crSize;

        
        private readonly Stream _stream;

        public const int ReadBufferSize = 4 * 1024 * 1024;
        private SimpleObjectPool<byte[]> _byteBufferPool = new SimpleObjectPool<byte[]>(() => new byte[ReadBufferSize]);
        private readonly SimpleObjectPool<int[]> _intArrayPool = new SimpleObjectPool<int[]>(() => new int[16384]);


        internal class BufferReadResult : IDisposable
        {
            public byte[] Buffer { get; internal set; }
            public int Length { get; internal set; }
            public long Position { get; internal set; }

            private readonly SimpleObjectPool<byte[]> _byteBufferPool;
            
            public BufferReadResult(SimpleObjectPool<byte[]> pool)
            {
                _byteBufferPool = pool;
                Buffer = pool.Get();
            }

            public void Dispose()
            {
                _byteBufferPool.Release(Buffer);
            }
        }

        public RawLineReader(Stream stream, Encoding encoding, SimpleObjectPool<byte[]> byteBufferPool)
        {
            _stream= stream;
            
            _rBytes = encoding.GetBytes("\r");
            _nBytes = encoding.GetBytes("\n");
            _crSize = _rBytes.Length;
        }

        public void StartReadRawLines(BlockingCollection<Task<CrlfSearcher.Result>> rawLineCollection)
        {
            var buffersCollection = new BlockingCollection<BufferReadResult>(10);
            var _ = ReadBuffers(_stream, buffersCollection);
            var crlfSearcher = new CrlfSearcher(_rBytes, _nBytes, _crSize);

            var lineCount = 0;
            foreach (var rawLineReaderResult in buffersCollection.GetConsumingEnumerable())
            {
                rawLineCollection.Add(
                    Task.Factory.StartNew(() =>
                    {
                        var result = crlfSearcher.ParseBuffer(rawLineReaderResult);
                        lineCount += result.ResultCount;
                        return result;
                    }));
            }

            Debug.WriteLine($"Total raw lines read: {lineCount}");

            rawLineCollection.CompleteAdding();
        }

        private async Task ReadBuffers(Stream stream, BlockingCollection<BufferReadResult> results)
        {
            var position = stream.Position;
            while (position < stream.Length - _crSize)
            {
                var result = new BufferReadResult(_byteBufferPool) {Position = position};
                var buffer = result.Buffer;

                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                var lastLineStart = FindLastLineStart(buffer, bytesRead);
                if (lastLineStart > 0)
                {
                    result.Length = lastLineStart;
                    results.Add(result);
                    stream.Position = position + lastLineStart;
                }
                else
                {
                    if (bytesRead < buffer.Length)
                    {
                        result.Length = bytesRead;
                        results.Add(result);
                        break;
                    }
                    
                    // TODO : process correctly
                    result.Length = buffer.Length - 1024;
                    results.Add(result);
                    stream.Position = position + buffer.Length - 1024;
                }

                position = stream.Position;
            }

            results.CompleteAdding();
        }

        private int FindLastLineStart(byte[] buffer, int searchFrom)
        {
            var position = searchFrom - _crSize;
            while (true)
            {
                if (position <= 0)
                    
                    return -1;

                var isCrlf = ByteArrayTools.FastEquals(buffer, position, _rBytes, 0, _crSize) ||
                             ByteArrayTools.FastEquals(buffer, position, _nBytes, 0, _crSize);
                if (isCrlf)
                    return position + _crSize;

                position = position - _crSize;
            }
        }
    }
}
