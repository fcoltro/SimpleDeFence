using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace SimpleDeFence.Utilities
{
    /// <summary>
    /// Minimal read/write of the .resx string entries, over plain XML.
    ///
    /// Replaces ResXResourceReader/Writer/ResXDataNode. Those are the natural API for this, but
    /// they live in the System.Windows.Forms assembly, and the DevelTool's "Optimize" feature was
    /// the only thing in the entire project still reaching into WinForms - a UI framework the app
    /// stopped using when the WinForms GUI was deleted. A .resx is XML, and the entries this tool
    /// touches are localization strings, so the format API buys nothing here that XDocument does
    /// not.
    ///
    /// Deliberately limited to string entries. A .resx can also carry binary or typed resources
    /// (via ResXFileRef or a serialized payload); those are read as null by <see cref="Read"/> and
    /// never written by <see cref="Write"/>, because a tool that rewrites a localization satellite
    /// has no business round-tripping a serialized object it cannot interpret.
    /// </summary>
    public static class ResxFile
    {
        /// <summary>The schema preamble a .resx needs to be loadable by the resource compiler.
        /// Reproduced rather than copied wholesale from an existing file so that writing does not
        /// depend on having one to hand.</summary>
        private const string ResHeaderResMimeType = "text/microsoft-resx";
        private const string ResHeaderVersion = "2.0";
        private const string ResHeaderReader = "System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";
        private const string ResHeaderWriter = "System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";

        /// <summary>Name to value for every string entry. Entries whose value is not a plain string
        /// (they carry a type or mimetype attribute) map to null, so a caller can tell "absent" from
        /// "present but not a string".</summary>
        public static Dictionary<string, string?> Read(string filePath)
        {
            var result = new Dictionary<string, string?>(StringComparer.Ordinal);
            var root = XDocument.Load(filePath).Root;
            if (root is null)
                return result;

            foreach (var data in root.Elements("data"))
            {
                var name = (string?)data.Attribute("name");
                if (string.IsNullOrEmpty(name))
                    continue;

                var isPlainString = data.Attribute("type") is null && data.Attribute("mimetype") is null;
                result[name!] = isPlainString ? (data.Element("value")?.Value ?? string.Empty) : null;
            }
            return result;
        }

        /// <summary>Writes a .resx containing exactly the given string entries, in the order given.
        /// xml:space="preserve" on every value, matching what the resource compiler expects and what
        /// ResXResourceWriter emitted - without it, leading or trailing spaces in a translation are
        /// silently lost.</summary>
        public static void Write(string filePath, IEnumerable<KeyValuePair<string, string>> entries)
        {
            XNamespace xml = "http://www.w3.org/XML/1998/namespace";

            var root = new XElement("root",
                new XElement("resheader", new XAttribute("name", "resmimetype"), new XElement("value", ResHeaderResMimeType)),
                new XElement("resheader", new XAttribute("name", "version"), new XElement("value", ResHeaderVersion)),
                new XElement("resheader", new XAttribute("name", "reader"), new XElement("value", ResHeaderReader)),
                new XElement("resheader", new XAttribute("name", "writer"), new XElement("value", ResHeaderWriter)));

            foreach (var entry in entries)
            {
                root.Add(new XElement("data",
                    new XAttribute("name", entry.Key),
                    new XAttribute(xml + "space", "preserve"),
                    new XElement("value", entry.Value)));
            }

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(filePath);
        }
    }
}
