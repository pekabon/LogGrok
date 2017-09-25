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
using System.Diagnostics;

namespace LogGrok.RegexParser
{
    public class RegexLineReader : ILineReader, IEnumerable<RegexLineReader.RegexBasedLine>
    {
        public struct RegexBasedLine : ILine
        {
            public RegexBasedLine(StringStorage storage, int[] groups, int groupsOffset, List<KeyValuePair<string, int>> groupNameMapping, long beginOffset, long endOffset)
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
                    for(var groupNum = 0; groupNum < _groupNameMapping.Count; groupNum++)
                    {
                        var kv = _groupNameMapping[groupNum];
                         if (kv.Key != s) continue;
                        var offset = groupNum * 2 + _groupsOffset;
                        var start = _groups[offset];
                        var len = _groups[offset + 1];
                        var result = new TextRange(start, len, _stringStorage);
                        return result;
                    }
                    return TextRange.Empty;
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

            //private readonly Dictionary<string, int> _groupNameMapping;
            private readonly List<KeyValuePair<string, int>> _groupNameMapping;
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

        private struct OnDisposeGuard : IDisposable
        {
            private readonly Action _action;
            public OnDisposeGuard(Action action) { _action = action; }
            public void Dispose() { _action(); }
        }

        IEnumerable<RegexBasedLine> GetRegexBasedLineEnumerable()
        {
            var cancellationTokenSource = new CancellationTokenSource();

            using (var taskCollection = new BlockingCollection<Task<(List<RegexBasedLine>, BufferParser.Result)>>(Environment.ProcessorCount * 2))
            {
                var readTask = Task.Factory.StartNew(() => ReadBuffers(taskCollection, cancellationTokenSource.Token), cancellationTokenSource.Token);

                void Cancel()
                {
                    try
                    {
                        if (!readTask.IsCompleted)
                        {
                            cancellationTokenSource.Cancel();
                            readTask.Wait();
                        }
                    }
                    catch (AggregateException excpt)
                    {
                        excpt.Handle(e => (e is OperationCanceledException) ? true : false);
                    }
                }

                using (new OnDisposeGuard(() => Cancel()))
                {
                    foreach (var task in taskCollection.GetConsumingEnumerable())
                    {
                        List<RegexBasedLine> lineList = null;
                        BufferParser.Result parseResult = null;

                        try
                        {
                            (lineList, parseResult) = task.Result;
                        }
                        catch(OperationCanceledException)
                        {
                            break;
                        }

                        using (parseResult)
                            foreach (var line in lineList)
                                yield return line;

                        lineList.Clear();
                        _listPool.Release(lineList);
                    }
                }
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
                        parseResult.GetGroupNameMappingList(idx), lineStart + position, lineStart + lineLength + position);
                    list.Add(line);
                }
                return (list, parseResult);
            }
        }

        private List<Task> CreateParsingWorkers(BlockingCollection<(BufferReadResult, TaskCompletionSource<(List<RegexBasedLine>, BufferParser.Result)>)> parsingQueue, CancellationToken token)
        {
            Task CreateParsingTask()
            {
                return Task.Factory.StartNew(() =>
                {
                    try
                    {
                        var bufferParser = new BufferParser(_encoding, _threadLocalRegexes.Value);
                        foreach (var item in parsingQueue.GetConsumingEnumerable(token))
                        {
                            (var bufferReadResult, var taskCompletionSource) = item;
                            var parseResult = ParseBuffer(bufferParser, bufferReadResult);
                            taskCompletionSource.SetResult(parseResult);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Debug.WriteLine("Parsing task cancelled gracefully");
                    }
                }, token, TaskCreationOptions.LongRunning, TaskScheduler.Current);
            }

            return Enumerable.Range(0, Environment.ProcessorCount).Select(_ => CreateParsingTask()).ToList();
        }

        private void ReadBuffers(BlockingCollection<Task<(List<RegexBasedLine>, BufferParser.Result)>> taskCollection, CancellationToken token)
        {
            var stream = _streamFactory();
            SkipPreamble(stream);
            var position = stream.Position;

            using (var parsingQueue = new BlockingCollection<(BufferReadResult, TaskCompletionSource<(List<RegexBasedLine>, BufferParser.Result)>)>())
            {
                var taskCreator = Task.Factory.StartNew(() => CreateParsingWorkers(parsingQueue, token), token);

                var tsk = new Task(() => CreateParsingWorkers(parsingQueue, token), token);

                using (new OnDisposeGuard(() => Task.WaitAll(taskCreator.Result.ToArray())))
                {
                    bool firstBuffer = true;
                    void ParseSynchronously(byte[] buffer, int lastLineStart, long pos)
                    {
                        var bufferParser = new BufferParser(_encoding, _threadLocalRegexes.Value);
                        var bufferReadResult = new BufferReadResult(_byteBufferPool, buffer, lastLineStart, pos);
                        var taskCompletionSource = new TaskCompletionSource<(List<RegexBasedLine>, BufferParser.Result)>();
                        var parseResult = ParseBuffer(bufferParser, bufferReadResult);
                        taskCompletionSource.SetResult(parseResult);
                        taskCollection.Add(taskCompletionSource.Task, token);
                    }

                    void PushToParsingQueue(byte[] buffer, int lastLineStart, long pos)
                    {
                        if (firstBuffer)
                        {
                            ParseSynchronously(buffer, lastLineStart, pos);
                            firstBuffer = false;
                            return;
                        }

                        var bufferReadResult = new BufferReadResult(_byteBufferPool, buffer, lastLineStart, pos);
                        var taskCompletionSource = new TaskCompletionSource<(List<RegexBasedLine>, BufferParser.Result)>();
                        parsingQueue.Add((bufferReadResult, taskCompletionSource), token);
                        taskCollection.Add(taskCompletionSource.Task, token);
                    }

                    bool ProcessNextBuffer(byte[] buffer, int toRead)
                    {
                        var bytesRead = stream.Read(buffer, 0, toRead);
                        var lastLineStart = FindLastLineStart(buffer, bytesRead);
                        if (lastLineStart > 0)
                        {
                            PushToParsingQueue(buffer, lastLineStart, position);
                            stream.Position = position + lastLineStart;
                        }
                        else
                        {
                            if (bytesRead < toRead)
                            {
                                PushToParsingQueue(buffer, bytesRead, position);
                                return false;
                            }

                            // TODO: correct
                            PushToParsingQueue(buffer, toRead - 1024, position);
                            stream.Position = position + toRead - 1024;
                        }
                        position = stream.Position;
                        return true;
                    }

                    ProcessNextBuffer(_byteBufferPool.Get(), 64 * 1024);

                    while (position < stream.Length - _crSize 
                        && !token.IsCancellationRequested)
                    {
                        var buffer = _byteBufferPool.Get();
                        if (!ProcessNextBuffer(buffer, buffer.Length))
                            break;

                    }

                    parsingQueue.CompleteAdding();
                    taskCollection.CompleteAdding();
                }
            }
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
