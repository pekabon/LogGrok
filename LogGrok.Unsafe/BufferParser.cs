using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Text.RegularExpressions;

namespace LogGrok.Unsafe
{
    public sealed class BufferParser : CrLfSearcherBase<BufferParser.Result>
    {
        private readonly Regex[] _regexes;
        private readonly Encoding _encoding;

        private readonly SimpleObjectPool<string> _stringPool = new SimpleObjectPool<string >(() => new string('\0', 4*1024*1025));
        private readonly SimpleObjectPool<int[]> _intArrayPool = new SimpleObjectPool<int[]>(() => new int[16384]);
        private readonly int _maxResultSize;
        private Dictionary<string, int>[] _groupMappings;
        private List<KeyValuePair<string, int>>[] _groupMappingList;

        private readonly Func<Match, int[][]> _matchesGetter  = GetFieldAccessor<Match, int[][]>("_matches");

        private static Func<T,R> GetFieldAccessor<T, R> (string fieldName) 
        { 
            var param = Expression.Parameter(typeof(T), "arg");
            var member = Expression.Field(param, fieldName);
            var lambda = Expression.Lambda(typeof(Func<T, R>), member, param);
            return (Func<T,R>)lambda.Compile(); 
        }

        public BufferParser(Encoding encoding, Regex[] regexes)
                : base(encoding.GetBytes("\r"), encoding.GetBytes("\n"), encoding.GetBytes("\n").Length)
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

            _groupMappingList = _groupMappings.Select(s => s.ToList()).ToArray();
        }
    

        public class Result : IDisposable
        {
            public byte[] Buffer { get; }
            public int[] ByteIndices { get; internal set; }

            public string StringBuffer { get; internal set; }
            public int[] StringIndices { get; internal set; }

            public int ResultCount { get; internal set; }

            public int LastBytePosition { get; internal set; }

            internal int StringBufferPosition { get; set; }
            internal int StringIndicesPosition { get; set; }

            public int GetLineStart(int i)
            {
                return ByteIndices[i * 2];
            }

            public int GetLineLength(int i)
            {
                return ByteIndices[i * 2 + 1];
            }

            public int GetStringIndicesOffset(int resultIndex)
            {
                return resultIndex* _maxResultSize + 1;
            }

            public Dictionary<string, int> GetGroupNameMapping(int resultIndex)
            {
                var regexIndex = StringIndices[resultIndex* _maxResultSize];
                return _groupMappings[regexIndex];
            }

            public List<KeyValuePair<string, int>> GetGroupNameMappingList(int resultIndex)
            {
                var regexIndex = StringIndices[resultIndex * _maxResultSize];
                return _groupMappingList[regexIndex];
            }

            public Result(byte[] buffer, SimpleObjectPool<string> stringPool, SimpleObjectPool<int[]> intArrayPool, int maxResultSize, Dictionary<string, int>[] groupMappings, List<KeyValuePair<string, int>>[] groupMappingList)
            {
                Buffer = buffer;
                ByteIndices = intArrayPool.Get();
                StringBuffer = stringPool.Get();
                StringIndices = intArrayPool.Get();
                _stringPool = stringPool;
                _intArrayPool = intArrayPool;
                _maxResultSize = maxResultSize;
                _groupMappings = groupMappings;
                _groupMappingList = groupMappingList;
            }

            public override string ToString()
            {
                var lastIndex = ResultCount - 1;
                return $"ResultCount = {ResultCount}, from = {ByteIndices[0]}, to = {GetLineStart(lastIndex) + GetLineLength(lastIndex)}";
            }

            public void Dispose()
            {
                _stringPool.Release(StringBuffer);
                _intArrayPool.Release(ByteIndices);
                _intArrayPool.Release(StringIndices);
            }

            private readonly SimpleObjectPool<string> _stringPool;
            private readonly SimpleObjectPool<int[]> _intArrayPool;
            private readonly Dictionary<string, int>[] _groupMappings;
            private List<KeyValuePair<string, int>>[] _groupMappingList;
            private readonly int _maxResultSize;
        }

        public Result ParseBuffer(byte[] buffer, int from, int len)
        {
            if (from + len > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(len));

            var result = new Result(buffer, _stringPool, _intArrayPool, _maxResultSize, _groupMappings, _groupMappingList);
            ProcessBuffer(buffer, from, len, result);
            return result;
        }

        public Result ParseBuffer(byte[] buffer)
        {
            return ParseBuffer(buffer, 0, buffer.Length);
        }
        
        protected override void ProcessLine(int start, int len, Result context)
        {
            var lineStart = context.StringBufferPosition;
            int lineLength;
            try
            {
                unsafe
                {
                    fixed (byte* bytes = context.Buffer)
                    fixed (char* chars = context.StringBuffer)
                    {
                        lineLength = _encoding.GetChars(bytes + start, len, chars + context.StringBufferPosition,
                            context.StringBuffer.Length - context.StringBufferPosition);
                        context.StringBufferPosition += lineLength;
                    }
                }
            }
            catch (ArgumentException)
            {
                context.StringBuffer = string.Concat(context.StringBuffer, context.StringBuffer);
                ProcessLine(start, len, context);
                return;
            }

            if ((context.ResultCount + 1)* 2 >= context.ByteIndices.Length)
            {
                var newByteIndices = new int[2 * context.ByteIndices.Length];
                Array.Copy(context.ByteIndices, 0, newByteIndices, 0, context.ResultCount*2);
                context.ByteIndices = newByteIndices;
            }
            
            if (context.StringIndicesPosition + _maxResultSize >= context.StringIndices.Length)
            {
                var newStringIndices = new int[2 * context.StringIndices.Length];
                Buffer.BlockCopy(context.StringIndices, 0, newStringIndices, 0, context.StringIndicesPosition);
                context.StringIndices = newStringIndices;
            }

            unchecked
            {
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

                        var matches = _matchesGetter(match);

                        foreach (var mtch in matches)
                        {
                            results[position] = mtch[0];
                            results[position + 1] = mtch[1];
                            position += 2;
                        }
                        //foreach (Group group in match.Groups)
                        //{
                        //    results[position] = group.Index;
                        //    results[position + 1] = group.Length;
                        //    position += 2;
                        //}

                        context.ByteIndices[context.ResultCount * 2] = start;
                        context.ByteIndices[context.ResultCount * 2 + 1] = len;

                        context.StringIndicesPosition += _maxResultSize;
                        context.ResultCount++;
                        break;

                    }
                    currentRegexIndex++;
                }

                context.LastBytePosition = start + len;
            }
        }
    }
}
