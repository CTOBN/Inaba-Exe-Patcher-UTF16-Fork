using Reloaded.Mod.Interfaces;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace p4gpc.inaba
{
    public class ExPatch
    {
        /// <summary>
        /// The name of the patch
        /// </summary>
        public string Name { get; set; }
        /// <summary>
        /// The pattern to sig scan for
        /// </summary>
        public string Pattern { get; set; }
        /// <summary>
        /// Text form of the pattern
        /// </summary>
        public string PatternText { get; set; }
        /// <summary>
        /// The function to add in the hook
        /// </summary>
        public string[] Function { get; set; }
        /// <summary>
        /// When to execute the function (first, after, or only)
        /// </summary>
        public string ExecutionOrder { get; set; }
        /// <summary>
        /// The offset to add to the address of the hook
        /// </summary>
        public int Offset { get; set; }
        /// <summary>
        /// If true this is a replacement instead of a code patch
        /// </summary>
        public bool IsReplacement { get; set; }
        /// <summary>
        /// If true when replacing values with strings the value will be padded with null characters up to the length of the search pattern
        /// </summary>
        public bool PadNull { get; set; }
        /// <summary>
        /// A list of indices to replace/patch at
        /// This will cause Inaba to scan multiple times and replace/patch each duplicate of it at these indices
        /// </summary>
        public List<int> Indices { get; set; }
        /// <summary>
        /// If true all occurences of the pattern will be replaced/patched
        /// </summary>
        public bool AllIndices { get; set; }
        /// <summary>
        /// What encoding type to use for search and replacement
        /// </summary>
        public Encoding EncodingSetting { get; set; }

        private CultureInfo Culture { get; set; }
        private ILogger Logger { get; set; }

        public ExPatch(string name, string pattern, string[] function, string executionOrder, int offset, bool isReplacement, bool padNull, List<int> indices, bool allIndices, Encoding encoding, CultureInfo culture, ILogger mLogger)
        {
            Name = name;
            PatternText = pattern;
            Function = function;
            ExecutionOrder = executionOrder;
            Offset = offset;
            IsReplacement = isReplacement;
            PadNull = padNull;
            Indices = indices;
            AllIndices = allIndices;
            EncodingSetting = encoding;
            Culture = culture;
            Logger = mLogger;

            Pattern = TranslatePattern(pattern);
        }

        private string TranslatePattern(string value)
        {
            if (int.TryParse(value, NumberStyles.Number, Culture, out int intValue))
            {
                var bytes = BitConverter.GetBytes(intValue);
                return BitConverter.ToString(bytes).Replace("-", " ");
            }
            else if (Regex.IsMatch(value, @"[0-9]+f") && float.TryParse(value, NumberStyles.Number, Culture, out float floatValue))
            {
                var bytes = BitConverter.GetBytes(floatValue);
                return BitConverter.ToString(bytes).Replace("-", " ");
            }
            else if (double.TryParse(value, NumberStyles.Number, Culture, out double doubleValue))
            {
                var bytes = BitConverter.GetBytes(doubleValue);
                return BitConverter.ToString(bytes).Replace("-", " ");
            }
            else
            {
                var stringValueMatch = Regex.Match(value, "\"(.*)\"");
                if (!stringValueMatch.Success)
                {
                    Logger.WriteLine($"[Inaba Exe Patcher] Unable to parse {value} as an int, double, float or string not creating search pattern");
                    return "";
                }
                string stringValue = Regex.Unescape(stringValueMatch.Groups[1].Value);
                var bytes = EncodingSetting.GetBytes(stringValue);
                return BitConverter.ToString(bytes).Replace("-", " ");
            }
        }
    }
}
