using LogGrok.LogParserBase;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LogGrok.Core;
using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using LogGrok.Unsafe;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Threading;

namespace LogGrok.RegexParser
{
    public class RegexLineReader : ILineReader, IEnumerable<RegexLineReader.RegexBasedLine>
    {
        public struct RegexBasedLine : ILine
        {
            public RegexBasedLine(StringStorage storage, int[] groups, int groupsOffset, Dictionary<string, int> groupNameMapping, long beginOffset, long endOffset)
            {
                _stringStorage = storage;
                _groups = groups;
                _groupsOffset = groupsOffset;
                _groupNameMapping = groupNameMapping;
                _beginOffset = beginOffset;
                _endOffset = endOffset;
            }

            public TextRange this[string s]
            { 
                get
                {
                    if (!_groupNameMapping.TryGetValue(s, out var groupNum))
                    {
                        return TextRange.Empty;
                    }

                    var offset = groupNum * 2 + _groupsOffset;
                    var start = _groups[offset];
                    var len = _groups[offset + 1];
                    var result = new TextRange(start, len, _stringStorage);
                    return result;
                }
            }

            public long Offset
            {
                get => _beginOffset;
                set => throw new NotSupportedException();
            }
            public long EndOffset
            {
                get => _endOffset;
                set => throw new NotSupportedException();
            }

            public TimeSpan Time => TimeSpan.Zero;

            public string RawLine =>
                new TextRange(_groups[_groupsOffset], _groups[_groupsOffset + 1], _stringStorage).ToString();

            public RegexBasedLine Clone()
            {
                var groups = new List<int>();
                var builder = new StringBuilder();
                var ln = this;
                var strings = _groupNameMapping.Select(kv => ln[kv.Key].ToString());

                var current = 0;
                foreach (var str in strings)
                {
                    builder.Append(str);
                    groups.Add(current);
                    groups.Add(str.Length);
                    current += str.Length;
                }
                
                return new RegexBasedLine(new StringStorage(builder.ToString()), groups.ToArray(), 0, _groupNameMapping, 0, 0);
            }

            private readonly int[] _groups;
            private readonly int _groupsOffset;
            private readonly Dictionary<string, int> _groupNameMapping;
            private readonly long _beginOffset;
            private readonly long _endOffset;
            private readonly StringStorage _stringStorage;
        }

        public RegexLineReader(Func<Stream> streamFactory, Encoding encoding, IEnumerable<Regex> regexes, MetaInformation meta)
        {
            _meta = meta;
            _encoding = encoding;
            _lineStream = streamFactory();
            _streamFactory = streamFactory;

             _threadLocalRegexes = new ThreadLocal<Regex[]>(() => regexes.Select(r => new Regex(r.ToString(), r.Options)).ToArray());
            _lineBufferParser = new BufferParser(encoding, regexes.ToArray());

            _crSize = encoding.GetByteCount("\r");
            _nBytes = encoding.GetBytes("\n");
            _rBytes = encoding.GetBytes("\r");
        }

        public void Dispose()
        {
        }

        public ILine GetLastLine()
        {
            return EmptyLine;
        }

        public ILine ReadLineAt(long beginOffset, long endOffset)
        {
            var len = (int)(endOffset - beginOffset);
            var buffer = _byteBufferPool.Get();

            _lineStream.Position = beginOffset;
            var bytesread = _lineStream.Read(buffer, 0, len);
            var bufferReadResult = new BufferReadResult(_byteBufferPool, buffer, bytesread, beginOffset);

            (var lineList, var parseResult)= ParseBuffer(_lineBufferParser, bufferReadResult);
            try
            {
                if (lineList.Count > 0)
                {
                    return lineList[0].Clone();
                }
                else
                {
                    return EmptyLine;
                }
            }
            finally
            {
                parseResult.Dispose();
                lineList.Clear();
                _listPool.Release(lineList);
            }
        }

        IEnumerable<RegexBasedLine> GetRegexBasedLineEnumerable()
        {
            var taskCollection = new BlockingCollection<Task<(List<RegexBasedLine>, BufferParser.Result)>>(Environment.ProcessorCount*2);
            Task.Factory.StartNew(() => ReadBuffers(taskCollection), TaskCreationOptions.LongRunning);

            foreach (var task in taskCollection.GetConsumingEnumerable())
            {
                (var lineList, var parseResult) = task.Result;

                using (parseResult)
                    foreach (var line in lineList)
                        yield return line;

                lineList.Clear();
                _listPool.Release(lineList);
            }
        }

        IEnumerator<ILine> IEnumerable<ILine>.GetEnumerator()
        {
            return GetRegexBasedLineEnumerable().Cast<ILine>().GetEnumerator();
        }
        
        IEnumerator<RegexBasedLine> IEnumerable<RegexBasedLine>.GetEnumerator()
        {
            return GetRegexBasedLineEnumerable().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetRegexBasedLineEnumerable().GetEnumerator();
        }

        private void SkipPreamble(Stream stream) 
        {
            stream.Position = 0;
            var preamble = _encoding.GetPreamble();
            var preambleBuffer = new byte[preamble.Length];
            var read = stream.Read(preambleBuffer, 0, preambleBuffer.Length);

            if(read < preambleBuffer.Length || 
                    !ByteArrayTools.FastEquals(preamble, 0, preambleBuffer, 0, preamble.Length))
            {
                stream.Position = 0;
            }
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

        internal struct BufferReadResult : IDisposable
        {
            public byte[] Buffer { get; }
            public int Length { get; }
            public long Position { get; }

            private readonly SimpleObjectPool<byte[]> _byteBufferPool;

            public BufferReadResult(SimpleObjectPool<byte[]> pool, byte[] buffer, int length, long position)
            {
                _byteBufferPool = pool;
                Buffer = pool.Get();

                Buffer = buffer; Length = length; Position = position;
            }

            public void Dispose()
            {
                _byteBufferPool.Release(Buffer);
            }
        }

        (List<RegexBasedLine>, BufferParser.Result) ParseBuffer(BufferParser bufferParser, BufferReadResult bufferReadResult)
        {
            var list = _listPool.Get();
            using (bufferReadResult)
            {
                var parseResult = bufferParser.ParseBuffer(bufferReadResult.Buffer, 0, bufferReadResult.Length);
                var stringStorage = new StringStorage(parseResult.StringBuffer);
                for (var idx = 0; idx < parseResult.ResultCount; idx++)
                {
                    var lineStart = parseResult.GetLineStart(idx);
                    var lineLength = parseResult.GetLineLength(idx);
                    var position = bufferReadResult.Position;
                    var line = new RegexBasedLine(stringStorage, parseResult.StringIndices, parseResult.GetStringIndicesOffset(idx),
                        parseResult.GetGroupNameMapping(idx), lineStart + position, lineStart + lineLength + position);
                    list.Add(line);
                }
                return (list, parseResult);
            }
        }

        private void ReadBuffers(BlockingCollection<Task<(List<RegexBasedLine>, BufferParser.Result)>> taskCollection)
        {
            var stream = _streamFactory();
            SkipPreamble(stream);
            var position = stream.Position;

            var parsingQueue = new BlockingCollection<(BufferReadResult, TaskCompletionSource<(List<RegexBasedLine>, BufferParser.Result)>)>();
            for (var idx = 0; idx < Environment.ProcessorCount; idx++)
            {
                Task.Factory.StartNew(() =>
                {
                    var bufferParser = new BufferParser(_encoding, _threadLocalRegexes.Value);

                    foreach (var item in parsingQueue.GetConsumingEnumerable())
                    {
                        (var bufferReadResult, var taskCompletionSource) = item;
                        var parseResult = ParseBuffer(bufferParser, bufferReadResult);
                        taskCompletionSource.SetResult(parseResult);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            void CreateParseTask(byte[] buffer, int lastLineStart, long pos)
            {
                var bufferReadResult = new BufferReadResult(_byteBufferPool, buffer, lastLineStart, pos);
                var taskCompletionSource = new TaskCompletionSource<(List<RegexBasedLine>, BufferParser.Result)>();
                parsingQueue.Add((bufferReadResult, taskCompletionSource));
                taskCollection.Add(taskCompletionSource.Task);
            }

            while (position < stream.Length - _crSize)
            {
                var buffer = _byteBufferPool.Get();
                var bytesRead = stream.Read(buffer, 0, buffer.Length);
                var lastLineStart = FindLastLineStart(buffer, bytesRead);
                if (lastLineStart > 0)
                {
                    CreateParseTask(buffer, lastLineStart, position);
                    stream.Position = position + lastLineStart;
                }
                else
                {
                    if (bytesRead < buffer.Length)
                    {
                        CreateParseTask(buffer, bytesRead, position);
                        break;
                    }

                    CreateParseTask(buffer, buffer.Length - 1024, position);
                    stream.Position = position + buffer.Length - 1024;
                }
                position = stream.Position;
            }

            parsingQueue.CompleteAdding();
            taskCollection.CompleteAdding();
        }

        
        private class EmptyLinePrivate : ILine
        {
            public TextRange this[string s] => TextRange.Empty;

            public TimeSpan Time => TimeSpan.Zero;

            public string RawLine => string.Empty;

            public long Offset { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
            public long EndOffset { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        }

        public const int ReadBufferSize = 4 * 1024 * 1024;
        private SimpleObjectPool<byte[]> _byteBufferPool = new SimpleObjectPool<byte[]>(() => new byte[ReadBufferSize]);
        private SimpleObjectPool<List<RegexBasedLine>> _listPool = new SimpleObjectPool<List<RegexBasedLine>>(() => new List<RegexBasedLine>());
        private static ILine EmptyLine = new EmptyLinePrivate();
        private MetaInformation _meta;
        private Encoding _encoding;
        private Stream _lineStream;
        private Func<Stream> _streamFactory;

        private ThreadLocal<Regex[]> _threadLocalRegexes;
        private BufferParser _lineBufferParser;

        private int _crSize;
        private byte[] _nBytes;
        private byte[] _rBytes;
    }
}
