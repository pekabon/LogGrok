using LogGrok.Resources.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

namespace LogGrok.AutoAnalyzer
{
    public sealed class SimpleLinesAnalyzer
    {
        Dictionary<Regex, string> dictionary = new Dictionary<Regex, string>();
        private static readonly Lazy<SimpleLinesAnalyzer> lazy =
           new Lazy<SimpleLinesAnalyzer>(() => new SimpleLinesAnalyzer());

        public static SimpleLinesAnalyzer Instance { get { return lazy.Value; } }

        private SimpleLinesAnalyzer()
        {
            try
            {
                ItemsList items = null;

                var serializer = new XmlSerializer(typeof(ItemsList));
                
                if (File.Exists(Settings.Default.AnalyzeSettingsFile))
                {
                    using (var reader = XmlReader.Create(Settings.Default.AnalyzeSettingsFile))
                    {
                        items = (ItemsList)serializer.Deserialize(reader);
                        dictionary = items.Item.ToDictionary(x => new Regex(x.Expression), x => x.Text);
                    }
                }
            }
            catch(Exception e) //suppress reading error
            {

            }
        }

        public string Analyze(string inputString)
        {
            if (string.IsNullOrEmpty(inputString))
                return "";
            return string.Join("; ", dictionary.Where(x =>
            {
                bool isMatch = false;
                try
                {
                    isMatch = x.Key.IsMatch(inputString);
                }
                catch (Exception) { } //suppress incorrect lambda opearation
                return isMatch;
            }
            ).Select(x => "[" + x.Value + "]"));
        }

        public bool TryToAnalyze(string inputString, out string outString)
        {
            outString = Analyze(inputString);
            return !string.IsNullOrEmpty(outString);
        }

        public bool IsEmpty()
        {
            return dictionary.Count == 0;
        }
    }
}
