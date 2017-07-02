using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LogGrok.Unsafe
{
    public sealed class RegexParser
    {
        private readonly Regex[] _regexes;
        private readonly Encoding _encoding;

        private readonly SimpleObjectPool<string> _stringPool = new SimpleObjectPool<string>(() => new string('\0', 1024 * 1024));
        private readonly SimpleObjectPool<int[]> _intArrayPool = new SimpleObjectPool<int[]>(() => new int[16384]);
        private readonly int _maxResultSize;
        private readonly Dictionary<string, int>[] _groupMappings;

        public RegexParser(Encoding encoding, Regex[] regexes)
        {
            _encoding = encoding;
            _regexes = regexes;

            var resultSizes = _regexes.Select(r => 2 * r.GetGroupNumbers().Length + 1).ToArray();
            _maxResultSize = resultSizes.Max();

            _groupMappings = _regexes.Select(regex =>
                Enumerable.Zip(
                        regex.GetGroupNames(),
                        regex.GetGroupNumbers(), Tuple.Create)
                    .ToDictionary(n => n.Item1, n => n.Item2)).ToArray();
        }


        public class Result : IDisposable
        {
            public CrlfSearcher.Result CrlfsResult { get; internal set; }
            
            public string StringBuffer { get; internal set; }
            public int[] StringIndices { get; internal set; }

            public long Position => CrlfsResult.Position;

            public int ResultCount { get; internal set; }

            public int LastBytePosition { get; internal set; }

            internal int StringBufferPosition { get; set; }
            internal int StringIndicesPosition { get; set; }

            public int GetLineStart(int i)
            {
                return CrlfsResult.ByteIndices[i * 2];
            }

            public int GetLineLength(int i)
            {
                return CrlfsResult.ByteIndices[i * 2 + 1];
            }

            public int GetStringIndicesOffset(int resultIndex)
            {
                return resultIndex * _maxResultSize + 1;
            }

            public Dictionary<string, int> GetGroupNameMapping(int resultIndex)
            {
                var regexIndex = StringIndices[resultIndex * _maxResultSize];
                return _groupMappings[regexIndex];
            }

            public Result(CrlfSearcher.Result crlfsResult, SimpleObjectPool<string> stringPool, SimpleObjectPool<int[]> intArrayPool, int maxResultSize, Dictionary<string, int>[] groupMappings)
            {
                CrlfsResult = crlfsResult;
                StringBuffer = stringPool.Get();
                StringIndices = intArrayPool.Get();
                _stringPool = stringPool;
                _intArrayPool = intArrayPool;
                _maxResultSize = maxResultSize;
                _groupMappings = groupMappings;
            }


            public void Dispose()
            {
                _stringPool.Release(StringBuffer);
                _intArrayPool.Release(StringIndices);
                CrlfsResult.Dispose();
            }

            private readonly SimpleObjectPool<string> _stringPool;
            private readonly SimpleObjectPool<int[]> _intArrayPool;
            private readonly Dictionary<string, int>[] _groupMappings;
            private readonly int _maxResultSize;
        }

        public Result Parse(CrlfSearcher.Result crlfsResult)
        {
            var result = new Result(crlfsResult, _stringPool, _intArrayPool, _maxResultSize, _groupMappings);
            Process(0, result);
            return result;
        }

        public void Process(int from, Result context)
        {
            unsafe
            {
                fixed (byte* bytes = context.CrlfsResult.Buffer)
                fixed (char* chars = context.StringBuffer)
                {
                    for (int i = from; i < context.CrlfsResult.ResultCount; i++)
                    {
                        var start = context.GetLineStart(i);
                        var len = context.GetLineLength(i);
                        var lineStart = context.StringBufferPosition;
                        int lineLength;
                        try
                        {
                            lineLength = _encoding.GetChars(bytes + start, len, chars + context.StringBufferPosition,
                                context.StringBuffer.Length - context.StringBufferPosition);
                            context.StringBufferPosition += lineLength;

                        }
                        catch (ArgumentException)
                        {
                            context.StringBuffer = string.Concat(context.StringBuffer, context.StringBuffer);
                            Process(i, context);
                            return;
                        }

                        if (context.StringIndicesPosition + _maxResultSize >= context.StringIndices.Length)
                        {
                            var newStringIndices = new int[2 * context.StringIndices.Length];
                            Buffer.BlockCopy(context.StringIndices, 0, newStringIndices, 0,
                                context.StringIndicesPosition);
                            context.StringIndices = newStringIndices;
                        }

                        var currentRegexIndex = 0;
                        foreach (var regex in _regexes)
                        {
                            var match = regex.Match(context.StringBuffer, lineStart, lineLength);
                            if (match.Success)
                            {
                                var results = context.StringIndices;
                                var position = context.StringIndicesPosition;
                                results[position] = currentRegexIndex;
                                position++;
                                foreach (Group group in match.Groups)
                                {
                                    results[position] = group.Index;
                                    results[position + 1] = group.Length;
                                    position += 2;
                                }
                                context.StringIndicesPosition += _maxResultSize;
                                context.ResultCount++;

                                if (context.ResultCount > context.CrlfsResult.ResultCount)
                                    Console.Write("");

                                break;
                            }
                            currentRegexIndex++;
                        }
                        context.LastBytePosition = start + len;
                    }
                }
            }
        }
    }
}