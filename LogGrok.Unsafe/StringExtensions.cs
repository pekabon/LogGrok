using System;

namespace LogGrok.Unsafe
{
    public static class StringExtensions
    {
        public static void UnsafeCopyTo(this string source, int begin, string target, int targetBegin, int length)
        {
            if (targetBegin + length > target.Length || begin + length > source.Length)
                throw new ArgumentOutOfRangeException(nameof(length));

            unsafe
            {
                fixed (char* sourceChars = source)
                fixed (char* targetChars = target)
                {
                    for (var i = 0; i < length; i++)
                    {
                        targetChars[i + targetBegin] = sourceChars[i + begin];
                    }
                }
            }
        }

        public static int GetHashCode(this string str, int from, int len)
        {
            unchecked
            {
                unsafe
                {
                    fixed (char* ptr = str)
                    {
                        var p = 16777619;
                        var hash = (int) 2166136261;

                        for (var i = from; i < from + len; i++)
                            hash = (hash ^ ((short*)ptr)[i]) * p;

                        hash += hash << 13;
                        hash ^= hash >> 7;
                        hash += hash << 3;
                        hash ^= hash >> 17;
                        hash += hash << 5;
                        return hash;
                    }
                }
            }
        }
    }
}
