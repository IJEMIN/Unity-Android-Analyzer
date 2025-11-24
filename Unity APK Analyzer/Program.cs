using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace UnityAndroidAnalyzer
{
    partial class Program
    {
        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            Console.WriteLine("=== Unity Android Analyzer ===");
            Console.WriteLine();

            while (true)
            {
                Console.WriteLine("Select mode:");
                Console.WriteLine("  [1] Analyze existing local APK/OBB files");
                Console.WriteLine("  [2] Extract APK/OBB from connected Android device");
                Console.WriteLine("  [q] Quit");
                Console.WriteLine();
                Console.Write("Enter choice: ");

                var mode = Console.ReadLine()?.Trim();
                Console.WriteLine();

                if (mode == "1")
                {
                    LocalMode();
                }
                else if (mode == "2")
                {
                    DeviceMode();
                }
                else if (mode is "q" or "Q")
                {
                    break;
                }
                else
                {
                    Console.Error.WriteLine("Invalid choice.");
                }

                Console.WriteLine();
            }

            Console.WriteLine("Bye !");
        }

        // ======================================================
        // Local mode (APK/OBB only)
        // ======================================================

        static void LocalMode()
        {
            while (true)
            {
                Console.Write("Enter path to APK file (or empty to finish): ");
                var apk = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(apk))
                {
                    Console.Error.WriteLine("[*] Local analysis finished.");
                    break;
                }

                if (!File.Exists(apk))
                {
                    Console.Error.WriteLine($"[!] File not found: {apk}");
                    continue;
                }

                var containers = new List<string> { apk };

                Console.Write("Does this app have OBB expansion files? (y/n): ");
                var ans = (Console.ReadLine() ?? string.Empty).Trim().ToLowerInvariant();
                if (ans is "y" or "yes")
                {
                    while (true)
                    {
                        Console.Write("Enter path to an OBB file (or empty if no more): ");
                        var obb = Console.ReadLine()?.Trim();
                        if (string.IsNullOrEmpty(obb))
                            break;

                        if (!File.Exists(obb))
                        {
                            Console.Error.WriteLine($"[!] OBB file not found: {obb}");
                            continue;
                        }

                        containers.Add(obb);
                    }
                }

                PrintMarkdownReport($"Local: {Path.GetFileName(apk)}", containers);
            }
        }

        // ======================================================
        // Device mode (ADB: list packages, pull APK/OBB, analyze)
        // ======================================================

        static void DeviceMode()
        {
            if (!CheckCommandExists("adb"))
            {
                Console.Error.WriteLine("Error: 'adb' not found in PATH.");
                return;
            }

            var adb = new AdbHelper();
            if (!adb.SelectDevice()) return;

            Console.Write("Enter keyword to search in package name or app label: ");
            var keyword = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(keyword))
            {
                Console.Error.WriteLine("Keyword is empty.");
                return;
            }

            Console.Error.WriteLine($"[*] Searching for packages containing '{keyword}'...");

            var pkgs = adb.SearchPackages(keyword);
            if (pkgs.Count == 0)
            {
                Console.Error.WriteLine($"No packages found for keyword: {keyword}");
                return;
            }

            for (var i = 0; i < pkgs.Count; i++)
            {
                var p = pkgs[i];
                Console.Error.WriteLine(
                    string.IsNullOrEmpty(p.Label)
                        ? $"  [{i + 1}] {p.PackageName}"
                        : $"  [{i + 1}] {p.PackageName} (label: {p.Label})");
            }

            Console.Write("Select package number: ");
            var choiceStr = Console.ReadLine()?.Trim();
            if (!int.TryParse(choiceStr, out var choice) || choice < 1 || choice > pkgs.Count)
            {
                Console.Error.WriteLine("Invalid choice.");
                return;
            }

            var selected = pkgs[choice - 1];
            Console.Error.WriteLine($"[*] Selected package: {selected.PackageName}");

            // Folder name from label
            var folderName = SanitizeFolderName(selected.Label ?? selected.PackageName);
            Directory.CreateDirectory(folderName);

            var containers = new List<string>();

            // Pull APKs
            var apkPaths = adb.GetApkPaths(selected.PackageName);
            if (apkPaths.Count > 0)
            {
                Console.Error.WriteLine("[*] Pulling APK files...");
                foreach (var remote in apkPaths)
                {
                    var local = Path.Combine(folderName, Path.GetFileName(remote));
                    Console.Error.WriteLine($"  -> {remote}");
                    adb.Pull(remote, local);
                    containers.Add(local);
                }
            }
            else
            {
                Console.Error.WriteLine("[*] No APK paths found for this package.");
            }

            // Pull OBBs
            var obbPaths = adb.GetObbPaths(selected.PackageName);
            if (obbPaths.Count > 0)
            {
                Console.Error.WriteLine($"[*] Found OBB directory on device: /sdcard/Android/obb/{selected.PackageName}");
                foreach (var remote in obbPaths)
                {
                    var local = Path.Combine(folderName, Path.GetFileName(remote));
                    Console.Error.WriteLine($"  -> {remote}");
                    adb.Pull(remote, local);
                    containers.Add(local);
                }
            }
            else
            {
                Console.Error.WriteLine("[*] No OBB directory found for this package (this is fine).");
            }

            if (containers.Count == 0)
            {
                Console.Error.WriteLine("No APK/OBB containers pulled, cannot analyze.");
                return;
            }

            PrintMarkdownReport($"Device: {selected.PackageName}", containers);
        }

        // ======================================================
        // ADB helper
        // ======================================================

        class PackageInfo
        {
            public string PackageName { get; set; } = "";
            public string? Label { get; set; }
        }

        // ======================================================
        // Static analysis core
        // ======================================================

        static void PrintMarkdownReport(string title, List<string> containerPaths)
        {
            var zipArchives = new List<ZipArchive>();

            foreach (var containerPath in containerPaths)
            {
                try
                {
                    var zipArchive = ZipFile.OpenRead(containerPath);
                    zipArchives.Add(zipArchive);
                } catch (Exception ex)
                {
                    Console.Error.WriteLine($"[!] Failed to open container '{containerPath}' as ZIP: {ex.Message}");
                }
            }
            
            if (zipArchives.Count == 0)
            {
                Console.Error.WriteLine("[!] No valid ZIP containers to analyze.");
                return;
            }
            
            try
            {
                var unityVersion = Analyzer.DetectUnityVersionFromContainers(zipArchives) ?? "Unknown";
                byte[]? metadataBytes = Analyzer.ExtractMetadataFromContainers(zipArchives);
                string rp = Analyzer.DetectRenderPipeline(metadataBytes);
                string entities = Analyzer.DetectEntities(metadataBytes);
                string addr = Analyzer.DetectAddressables(zipArchives) ? "yes" : "no";
                var nsList = Analyzer.DetectMajorNamespaces(metadataBytes);

                Console.WriteLine($"## {title}");
                Console.WriteLine();
                Console.WriteLine($"- **Unity Version:** `{unityVersion}`");
                Console.WriteLine($"- **Render Pipeline:** `{rp}`");
                Console.WriteLine($"- **Entities Used:** `{entities}`");
                Console.WriteLine($"- **Addressables Used:** `{addr}`");
                Console.WriteLine();
                Console.WriteLine("### Major Namespaces (top 20)");
                Console.WriteLine();

                if (nsList.Count == 0)
                {
                    Console.WriteLine("_No namespace information available (metadata not found or unreadable)._");
                }
                else
                {
                    foreach (var (ns, count) in nsList)
                    {
                        Console.WriteLine($"- `{ns}` ({count})");
                    }
                }

                Console.WriteLine();
                Console.WriteLine("---");
                Console.WriteLine();
            }
            finally
            {
                foreach (var z in zipArchives)
                {
                    z.Dispose();
                }
            }
        }

        // ======================================================
        // System helpers
        // ======================================================

        static bool CheckCommandExists(string command)
        {
            try
            {
                var (exit, _, _) = RunProcess(command, "--version", true, 2000);
                return exit == 0 || exit == 1; // some cmds return 1 for --version
            }
            catch
            {
                return false;
            }
        }

        static (int ExitCode, string Stdout, string Stderr) RunProcess(
            string fileName,
            string arguments,
            bool captureOutput,
            int timeoutMs = 60000)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = captureOutput,
                RedirectStandardError = captureOutput,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var proc = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            if (captureOutput)
            {
                proc.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                        stdout.AppendLine(e.Data);
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                        stderr.AppendLine(e.Data);
                };
            }

            proc.Start();
            if (captureOutput)
            {
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }

            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(true); } catch { /* ignore */ }
                throw new TimeoutException($"{fileName} {arguments} timed out.");
            }

            return (proc.ExitCode, stdout.ToString(), stderr.ToString());
        }

        static string SanitizeFolderName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
            {
                if (invalid.Contains(ch))
                    continue;
                sb.Append(ch);
            }
            var result = sb.ToString();
            return string.IsNullOrWhiteSpace(result) ? "app" : result.Trim();
        }
    }
}