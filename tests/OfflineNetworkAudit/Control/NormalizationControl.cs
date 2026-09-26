using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;

internal static class NormalizationControl
{
    public static void Run(string implementationAssembly, bool expectLexical)
    {
        var assemblyPath = Path.GetFullPath(implementationAssembly);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var dependency = Path.Combine(Path.GetDirectoryName(assemblyPath)!, name.Name + ".dll");
            return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
        };
        var fixture = Directory.CreateTempSubdirectory("jigglefin-normalize-audit-");
        try
        {
            var media = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Long media root"));
            var buffer = new StringBuilder(32768);
            if (GetShortPathName(media.FullName, buffer, buffer.Capacity) == 0 || buffer.ToString() == media.FullName)
            {
                throw new InvalidOperationException("This owned test needs existing 8.3 support; never change the volume setting.");
            }

            var input = buffer.ToString();
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
            var method = assembly.GetType("Emby.Server.Implementations.Library.Live.LiveDirectoryBrowser", throwOnError: true)!
                .GetMethod("NormalizeRootPath", BindingFlags.Public | BindingFlags.Static)!;
            var normalized = (string)method.Invoke(null, [input])!;
            if ((normalized == input) != expectLexical)
            {
                throw new InvalidOperationException("Normalization did not match the requested control: " + normalized);
            }

            Console.WriteLine("NORMALIZATION_INPUT:" + input);
            Console.WriteLine("Native short-name normalization control passed: " + (expectLexical ? "lexical" : "expanding"));
        }
        finally
        {
            fixture.Delete(true);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetShortPathName(string path, StringBuilder shortPath, int size);
}
