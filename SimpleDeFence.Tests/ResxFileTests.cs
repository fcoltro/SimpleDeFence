using System;
using System.Collections.Generic;
using System.IO;
using SimpleDeFence.Utilities;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class ResxFileTests : IDisposable
    {
        private readonly string _dir;

        public ResxFileTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sdf-resx-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string Write(string name, string body)
        {
            var p = Path.Combine(_dir, name);
            File.WriteAllText(p, body);
            return p;
        }

        private const string Header = """
            <?xml version="1.0" encoding="utf-8"?>
            <root>
              <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
              <resheader name="version"><value>2.0</value></resheader>
            """;

        [Fact]
        public void Reads_string_entries()
        {
            var p = Write("a.resx", Header + """
                  <data name="Greeting" xml:space="preserve"><value>Hello</value></data>
                  <data name="Farewell" xml:space="preserve"><value>Bye</value></data>
                </root>
                """);

            var read = ResxFile.Read(p);

            Assert.Equal("Hello", read["Greeting"]);
            Assert.Equal("Bye", read["Farewell"]);
        }

        [Fact]
        public void Non_string_entries_read_as_null_rather_than_being_misinterpreted()
        {
            // A typed or file-backed entry is present but not a string. Reporting it as an empty
            // string would let the optimiser treat it as a translation and rewrite it.
            var p = Write("b.resx", Header + """
                  <data name="Plain" xml:space="preserve"><value>text</value></data>
                  <data name="Picture" type="System.Resources.ResXFileRef"><value>img.png;System.Drawing.Bitmap</value></data>
                </root>
                """);

            var read = ResxFile.Read(p);

            Assert.Equal("text", read["Plain"]);
            Assert.True(read.ContainsKey("Picture"));
            Assert.Null(read["Picture"]);
        }

        [Fact]
        public void Round_trips_through_write_and_read()
        {
            var outPath = Path.Combine(_dir, "out.resx");
            ResxFile.Write(outPath, new[]
            {
                new KeyValuePair<string, string>("One", "Um"),
                new KeyValuePair<string, string>("Two", "Dois"),
            });

            var read = ResxFile.Read(outPath);

            Assert.Equal("Um", read["One"]);
            Assert.Equal("Dois", read["Two"]);
        }

        [Fact]
        public void Preserves_surrounding_whitespace_in_values()
        {
            // Without xml:space="preserve" the resource compiler trims these, silently changing a
            // translation.
            var outPath = Path.Combine(_dir, "ws.resx");
            ResxFile.Write(outPath, new[] { new KeyValuePair<string, string>("Padded", "  spaced  ") });

            Assert.Equal("  spaced  ", ResxFile.Read(outPath)["Padded"]);
        }

        [Fact]
        public void Preserves_multiline_and_non_ascii_values()
        {
            var outPath = Path.Combine(_dir, "i18n.resx");
            ResxFile.Write(outPath, new[]
            {
                new KeyValuePair<string, string>("Multi", "first\nsecond"),
                new KeyValuePair<string, string>("Accents", "Português – configuração"),
                new KeyValuePair<string, string>("Markup", "5 < 6 & \"quoted\""),
            });

            var read = ResxFile.Read(outPath);

            Assert.Equal("first\nsecond", read["Multi"]!.Replace("\r\n", "\n"));
            Assert.Equal("Português – configuração", read["Accents"]);
            Assert.Equal("5 < 6 & \"quoted\"", read["Markup"]);
        }

        [Fact]
        public void Written_files_carry_the_headers_the_resource_compiler_expects()
        {
            var outPath = Path.Combine(_dir, "hdr.resx");
            ResxFile.Write(outPath, new[] { new KeyValuePair<string, string>("K", "V") });

            var text = File.ReadAllText(outPath);

            Assert.Contains("text/microsoft-resx", text);
            Assert.Contains("ResXResourceReader", text);
            Assert.Contains("ResXResourceWriter", text);
        }

        [Fact]
        public void Reads_a_real_satellite_from_this_repository()
        {
            // The format this has to cope with in practice, not a fixture of my own making.
            var repoResx = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..",
                "SimpleDeFence", "Resources", "Messages.pt-BR.resx"));

            if (!File.Exists(repoResx))
                return; // running outside the repo layout

            var read = ResxFile.Read(repoResx);

            Assert.NotEmpty(read);
            Assert.Contains(read, kv => !string.IsNullOrEmpty(kv.Value));
        }
    }
}
