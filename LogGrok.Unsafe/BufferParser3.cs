using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LogGrok.Unsafe
{
    //public sealed class BufferParser3
    //{
    //    private readonly CrlfSearcher _crlfSearcher;
    //    private readonly RegexParser _regexParser;

    //    public BufferParser3(Encoding encoding, Regex[] regexes)
    //    {
    //        _crlfSearcher = new CrlfSearcher(encoding.GetBytes("\r"), encoding.GetBytes("\n"), encoding.GetBytes("\n").Length);
    //        _regexParser = new RegexParser(encoding, regexes);
    //    }

    //    public RegexParser.Result ParseBuffer(byte[] buffer, int from, int len)
    //    {
    //        if (from + len > buffer.Length)
    //            throw new ArgumentOutOfRangeException(nameof(len));

    //        var crlfResult = _crlfSearcher.ParseBuffer(buffer, from, len);
    //        var result = _regexParser.Parse(crlfResult);
    //        return result;
    //    }
    //}
}
