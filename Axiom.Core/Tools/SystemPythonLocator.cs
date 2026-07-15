using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Axiom.Core.Tools
{
    public sealed record PythonRuntimeInfo(string ExecutablePath, string SharedLibraryPath, string Version);

    // The WPF app bundles a portable CPython via Python.Included, which only ships a Windows
    // distribution. The CLI targets Linux/macOS too, so instead it discovers whatever system
    // Python 3 is already on PATH and points pythonnet's embedding at that interpreter's own
    // shared library — the standard cross-platform pythonnet deployment pattern.
    public static class SystemPythonLocator
    {
        public static bool TryLocate(out PythonRuntimeInfo? info, out string error)
        {
            string[] candidates = OperatingSystem.IsWindows()
                ? new[] { "python3", "python", "py" }
                : new[] { "python3", "python" };

            var attempts = new List<string>();
            foreach (string candidate in candidates)
            {
                if (TryProbe(candidate, out info, out string probeError))
                {
                    error = string.Empty;
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(probeError))
                    attempts.Add(probeError);
            }

            info = null;
            error = attempts.Count > 0
                ? "No usable system Python 3 installation was found. " + string.Join(" ", attempts)
                : "No system Python 3 installation was found on PATH. Install Python 3.9+ so 'python3' (or 'python') resolves on PATH.";
            return false;
        }

        private static bool TryProbe(string executable, out PythonRuntimeInfo? info, out string error)
        {
            info = null;
            error = string.Empty;

            const string script =
                "import sys, sysconfig\n" +
                "print(sys.executable)\n" +
                "print('%d.%d.%d' % sys.version_info[:3])\n" +
                "print(sysconfig.get_config_var('INSTSONAME') or '')\n" +
                "print(sysconfig.get_config_var('LDLIBRARY') or '')\n" +
                "print(sysconfig.get_config_var('LIBDIR') or '')\n" +
                "print(sysconfig.get_config_var('base') or sys.base_prefix)\n";

            Process? process = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(script);

                process = Process.Start(psi);
                if (process == null)
                    return false;

                string stdout = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                if (!process.HasExited || process.ExitCode != 0)
                    return false;

                string[] lines = stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                if (lines.Length < 6)
                    return false;

                string executablePath = lines[0].Trim();
                string version = lines[1].Trim();
                string instSoName = lines[2].Trim();
                string ldLibrary = lines[3].Trim();
                string libDir = lines[4].Trim();
                string basePrefix = lines[5].Trim();

                if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(version))
                    return false;

                string? sharedLib = ResolveSharedLibraryPath(instSoName, ldLibrary, libDir, basePrefix, executablePath, version);
                if (sharedLib == null)
                {
                    error = $"Found Python {version} at {executablePath} but could not locate its shared library for embedding.";
                    return false;
                }

                info = new PythonRuntimeInfo(executablePath, sharedLib, version);
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
            {
                return false;
            }
            finally
            {
                process?.Dispose();
            }
        }

        private static string? ResolveSharedLibraryPath(
            string instSoName,
            string ldLibrary,
            string libDir,
            string basePrefix,
            string executablePath,
            string version)
        {
            string? executableDir = Path.GetDirectoryName(executablePath);
            var candidateNames = new[] { instSoName, ldLibrary }
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var candidatePaths = new List<string>();
            foreach (string name in candidateNames)
            {
                if (!string.IsNullOrWhiteSpace(libDir))
                    candidatePaths.Add(Path.Combine(libDir, name));
                candidatePaths.Add(Path.Combine(basePrefix, "lib", name));
                if (!string.IsNullOrWhiteSpace(executableDir))
                    candidatePaths.Add(Path.Combine(executableDir, name));
            }

            // Windows' sysconfig rarely reports INSTSONAME/LDLIBRARY; the DLL instead sits next to
            // (or one level above, for a venv) python.exe. There are always two candidates —
            // "python3.dll" (the version-agnostic "limited stable ABI" DLL, which does NOT export
            // the full/internal C API pythonnet needs) and "python3XY.dll" (the real, fully
            // versioned DLL). The versioned name must be tried first, or pythonnet's embedding
            // fails at startup with a missing-symbol error even though a DLL was "found".
            if (OperatingSystem.IsWindows())
            {
                string[] versionParts = version.Split('.');
                if (versionParts.Length >= 2)
                {
                    string versionedName = $"python{versionParts[0]}{versionParts[1]}.dll";
                    foreach (string? dir in new[] { executableDir, basePrefix }.Where(d => !string.IsNullOrWhiteSpace(d)))
                        candidatePaths.Add(Path.Combine(dir!, versionedName));
                }

                foreach (string? dir in new[] { executableDir, basePrefix }.Where(d => !string.IsNullOrWhiteSpace(d)))
                {
                    if (!Directory.Exists(dir))
                        continue;

                    candidatePaths.AddRange(Directory.EnumerateFiles(dir!, "python3*.dll")
                        .Where(path => !string.Equals(Path.GetFileName(path), "python3.dll", StringComparison.OrdinalIgnoreCase)));
                }
            }
            else
            {
                // Homebrew and apt-installed Pythons frequently report empty/unhelpful
                // INSTSONAME+LDLIBRARY too (confirmed against a Homebrew 3.14 install in CI),
                // and Linux .so files often carry a soname suffix (libpython3.11.so.1.0) that a
                // plain "libpythonX.Y.so" guess won't match — so fall back to a directory scan.
                string[] versionParts = version.Split('.');
                string majorMinor = versionParts.Length >= 2 ? $"{versionParts[0]}.{versionParts[1]}" : string.Empty;
                string extension = OperatingSystem.IsMacOS() ? "dylib" : "so";

                var libDirs = new[] { libDir, Path.Combine(basePrefix, "lib") }
                    .Where(dir => !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (majorMinor.Length > 0)
                {
                    foreach (string dir in libDirs)
                    {
                        candidatePaths.Add(Path.Combine(dir, $"libpython{majorMinor}.{extension}"));
                        // Legacy "wide unicode"/pymalloc ABI flag some distros still use.
                        candidatePaths.Add(Path.Combine(dir, $"libpython{majorMinor}m.{extension}"));
                    }
                }

                foreach (string dir in libDirs)
                {
                    candidatePaths.AddRange(Directory.EnumerateFiles(dir, "libpython3*")
                        .Where(path => path.Contains('.' + extension, StringComparison.OrdinalIgnoreCase)));
                }

                // python.org / some Homebrew macOS builds are framework layouts: the loadable
                // image is the extensionless "Python" binary directly inside Versions/X.Y/.
                if (OperatingSystem.IsMacOS())
                {
                    candidatePaths.Add(Path.Combine(basePrefix, "Python"));
                    if (!string.IsNullOrWhiteSpace(executableDir))
                        candidatePaths.Add(Path.GetFullPath(Path.Combine(executableDir, "..", "Python")));
                }
            }

            return candidatePaths.FirstOrDefault(File.Exists);
        }
    }
}
