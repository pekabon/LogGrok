using System.Collections.Generic;
using System.Text;

namespace LogGrok.Unsafe
{
    public static class BufferConverter
    {
        public static void Convert(
            Encoding encoding, 
            IEnumerable<Nemerle.Builtins.Tuple<int, int>> offsets, 
            byte[] buffer, 
            string stringBuffer,
            List<Nemerle.Builtins.Tuple<int, int>> stringBufferOffsets)
        {
            var stringBufferPosition = 0;
            foreach (var offset in offsets)
            {
                var begin = offset.Field0;
                var len = offset.Field1;
                unsafe
                {
                    fixed(byte* bytes = buffer)
                    fixed (char* chars = stringBuffer)
                    {
                        var charCount = encoding.GetChars(bytes + begin, len, chars + stringBufferPosition, stringBuffer.Length);
                        stringBufferOffsets.Add(new Nemerle.Builtins.Tuple<int, int>(stringBufferPosition, charCount));
                        stringBufferPosition += charCount;
                    }
                }
            }
        }
    }
}
