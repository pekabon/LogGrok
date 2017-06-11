using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LogGrok.Unsafe
{
    public static class StringExtensions
    {
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
