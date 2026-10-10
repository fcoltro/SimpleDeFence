using System.IO;
using System.Runtime.CompilerServices;

namespace SimpleDeFence.Tests
{
    /// <summary>
    /// Locates files in this repository from the test sources themselves.
    ///
    /// The tests used to walk up from AppContext.BaseDirectory by a fixed number of levels and
    /// return early - passing - when the file was not where they expected it. A changed output path
    /// or a renamed file then turned every such test into one that could not fail, which is what
    /// happened to the resx satellite test once Messages.pt-BR.resx was deleted. The compiler
    /// records where this file was compiled from, and the tests always run on the machine that
    /// compiled them, so that path is a fixed anchor.
    /// </summary>
    internal static class TestRepo
    {
        public static string Root { get; } = FindRoot();

        /// <summary>Full path of a file in the repository. Fails the test if it is not there,
        /// rather than letting the caller skip its assertions.</summary>
        public static string File(params string[] relative)
        {
            var path = Path.Combine(Root, Path.Combine(relative));
            Xunit.Assert.True(System.IO.File.Exists(path), $"Expected repository file is missing: {path}");
            return path;
        }

        private static string FindRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));
    }
}
