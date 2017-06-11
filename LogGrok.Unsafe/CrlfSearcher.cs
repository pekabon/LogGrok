using System;
using System.Diagnostics;
using System.Linq;

namespace LogGrok.Unsafe
{
    public abstract class CrLfSearcher<T>
    {
        private readonly byte[] _r;
        private readonly byte[] _n;
        private readonly Patterns _crlfSearchPatterns;
        private readonly int _charSize;

        private class SearcherState
        {
            public int lastCrlf;
            public int crlfCount;
            public int current;
            public int lineStart;
        }

        private struct Patterns
        {
            public ulong rPattern;
            public ulong nPattern;
            public ulong minusPattern;
            public ulong andPattern;
        }

        protected CrLfSearcher(byte[] r, byte[] n, int charSize)
        {
            _r = r;
            _n = n;
            _charSize = charSize;
            _crlfSearchPatterns = GetPatterns(r, n);
        }

        protected abstract void ProcessLine(int start, int len, T context);

        public unsafe void ProcessBuffer(byte[] buffer, int from, int len, T context)
        {
            var searcherState = new SearcherState();
            fixed (byte* r = _r)
            fixed (byte* n = _n)
            fixed (byte* bytes = buffer)
            {
                var byteBuffer = bytes + from;
                var bufferSize = len;

                var mod = (int)(((long)byteBuffer) % 8);
                for (var i = 0; i < (mod - _charSize); i += _charSize)
                {
                    if (CompareTwo(r, n, byteBuffer, i, _charSize))
                    {
                        ProcessCrlf(i, searcherState, context);
                    }
                }

                var longsBuffer = (ulong*)(byteBuffer + mod);
                var longsCount = (bufferSize - mod) / 8;
                var patterns = _crlfSearchPatterns;
                for (var idx = 0; idx < longsCount; idx++)
                {
                    var v = longsBuffer[idx];
                    if (!HaveBytes(v, patterns.nPattern, patterns.minusPattern, patterns.andPattern) &&
                        !HaveBytes(v, patterns.rPattern, patterns.minusPattern, patterns.andPattern)) continue;

                    var  bytePosition = idx * 8 + mod;
                    for (var i = bytePosition; i <= bytePosition + 8 - _charSize; i += _charSize)
                    {
                        if (CompareTwo(r, n, byteBuffer, i, _charSize))
                            ProcessCrlf(i, searcherState, context);
                    }
                }

                for (var i = mod + longsCount * 8; i <= bufferSize - _charSize; i += _charSize)
                {
                    if (CompareTwo(r, n, byteBuffer, i, _charSize))
                        ProcessCrlf(i, searcherState, context);
                }

                ProcessCrlfs(searcherState, context);
                if (searcherState.lineStart > len)
                    ProcessLine(searcherState.lineStart, len - searcherState.lineStart, context);
            }
        }

        private void ProcessCrlfs(SearcherState searcherState, T context)
        {
            var s = searcherState;
            while (s.crlfCount > 0)
            {
                if (s.crlfCount < 3)
                {
                    var firstCrlf = s.lastCrlf - (s.crlfCount - 1) * _charSize;
                    var lineLength = (firstCrlf - s.lineStart) + s.crlfCount * _charSize;

                    ProcessLine(s.lineStart, lineLength, context);

                    s.lineStart += lineLength;
                    s.crlfCount = 0;
                }
                else
                {
                    var firstCrlf = s.lastCrlf - (s.crlfCount - 1) * _charSize;
                    var lineLength = (firstCrlf - s.lineStart) + 2 * _charSize;

                    ProcessLine(s.lineStart, lineLength, context);

                    s.lineStart += lineLength;
                    s.crlfCount -= 2;
                }
            }
        }

        private void ProcessCrlf(int crlfPosition, SearcherState searcherState, T context)
        {
            var s = searcherState;
            if ((crlfPosition - s.lastCrlf) <= _charSize || s.crlfCount == 0)
            {
                s.crlfCount++;
            }
            else
            {
                ProcessCrlfs(searcherState, context);
                s.crlfCount = 1;
            }

            s.lastCrlf = crlfPosition;
        }

        private static unsafe bool CompareTwo(byte* a, byte* b, byte* buffer, int bufferPos, int l)
        {
            var result1 = true;
            var result2 = true;
            for (var i = 0; i < l && (result1 || result2); i++)
            {
                var bbyte = buffer[i + bufferPos];
                result1 = a[i] == bbyte;
                result2 = b[i] == bbyte;
            }
            return result1 || result2;
        }

        private static bool HaveBytes(ulong value, ulong pattern, ulong minusPattern, ulong andPattern)
        {
            unchecked
            {
                var masked = value ^ pattern;
                return ((masked - minusPattern) & ~masked & andPattern) != 0;
            }
        }

        private static Patterns GetPatterns(byte[] r, byte[] n)
        {
            ulong ToLongBe(byte[] value) => 
                BitConverter.ToUInt64(value.Reverse().ToArray(), value.Length - sizeof(UInt64));

            var patternLength = r.Length;

            var rPattern = new byte[8];
            var nPattern = new byte[8];
            var minusPattern = new byte[8];
            var andPattern = new byte[8];
            
            for (var idx = 0; idx< 8; idx += patternLength)
            {
                Buffer.BlockCopy(r, 0, rPattern, idx, patternLength);
                Buffer.BlockCopy(n, 0, nPattern, idx, patternLength);
                minusPattern[idx + patternLength - 1] = 1;
                andPattern[idx] = 0x80;
            }

            return new Patterns
            {
                rPattern = BitConverter.ToUInt64(rPattern, 0),
                nPattern = BitConverter.ToUInt64(nPattern, 0),
                minusPattern = ToLongBe(minusPattern),
                andPattern = ToLongBe(andPattern)
            };
        }
    };

}
