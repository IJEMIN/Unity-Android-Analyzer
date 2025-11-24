using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityAndroidAnalyzer
{
    class Program
    {
        // ---- Config / Patterns ----
        private const string VersionPattern = @"20[0-9]{2}\.[0-9]+\.[0-9]+[a-z][0-9]*";
        private const string MetadataPath = "assets/bin/Data/Managed/Metadata/global-metadata.dat";

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
            if (!adb.SelectDevice())
                return;

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

            for (int i = 0; i < pkgs.Count; i++)
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

        class AdbHelper
        {
            public string? Serial { get; private set; }

            public bool SelectDevice()
            {
                var (exit, stdout, stderr) = RunProcess("adb", "devices", true);
                if (exit != 0)
                {
                    Console.Error.WriteLine($"Error running 'adb devices': {stderr}");
                    return false;
                }

                var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var devices = new List<string>();

                foreach (var line in lines)
                {
                    if (line.StartsWith("List of devices"))
                        continue;

                    var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && parts[1] == "device")
                        devices.Add(parts[0]);
                }

                if (devices.Count == 0)
                {
                    Console.Error.WriteLine("Error: No connected Android device found.");
                    return false;
                }

                if (devices.Count == 1)
                {
                    Serial = devices[0];
                    Console.Error.WriteLine($"[*] Using device: {Serial}");
                    return true;
                }

                Console.Error.WriteLine("[*] Multiple devices found:");
                for (int i = 0; i < devices.Count; i++)
                {
                    Console.Error.WriteLine($"  [{i + 1}] {devices[i]}");
                }

                Console.Write("Select device number: ");
                var choiceStr = Console.ReadLine()?.Trim();
                if (!int.TryParse(choiceStr, out var choice) || choice < 1 || choice > devices.Count)
                {
                    Console.Error.WriteLine("Invalid choice.");
                    return false;
                }

                Serial = devices[choice - 1];
                Console.Error.WriteLine($"[*] Using device: {Serial}");
                return true;
            }

            public List<PackageInfo> SearchPackages(string keyword)
            {
                var result = new List<PackageInfo>();

                var (exit, stdout, stderr) = RunAdb("shell pm list packages", true);
                if (exit != 0)
                {
                    Console.Error.WriteLine($"Error running 'pm list packages': {stderr}");
                    return result;
                }

                var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("package:"))
                        continue;
                    var pkg = trimmed.Substring("package:".Length);
                    if (pkg.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var label = GetLabel(pkg);
                    result.Add(new PackageInfo { PackageName = pkg, Label = label });
                }

                return result;
            }

            public List<string> GetApkPaths(string packageName)
            {
                var result = new List<string>();
                var (exit, stdout, stderr) = RunAdb($"shell pm path {packageName}", true);
                if (exit != 0)
                    return result;

                var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("package:"))
                        continue;
                    var path = trimmed.Substring("package:".Length);
                    result.Add(path);
                }
                return result;
            }

            public List<string> GetObbPaths(string packageName)
            {
                var result = new List<string>();
                var (exit, stdout, stderr) = RunAdb($"shell ls /sdcard/Android/obb/{packageName}", true);
                if (exit != 0)
                {
                    // likely no such dir, ignore
                    return result;
                }

                var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var name = line.Trim();
                    if (string.IsNullOrEmpty(name))
                        continue;
                    // some devices may print full paths, some just file names
                    if (!name.StartsWith("/"))
                        name = $"/sdcard/Android/obb/{packageName}/{name}";
                    result.Add(name);
                }

                return result;
            }

            public void Pull(string remote, string local)
            {
                var args = $"pull \"{remote}\" \"{local}\"";
                var (exit, _, stderr) = RunAdb(args, true);
                if (exit != 0)
                {
                    Console.Error.WriteLine($"[!] adb pull failed: {stderr}");
                }
            }

            public string? GetLabel(string packageName)
            {
                var (exit, stdout, stderr) = RunAdb($"shell dumpsys package {packageName}", true);
                if (exit != 0)
                    return null;

                var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var t = line.Trim();
                    // application-label:Something
                    if (t.Contains("application-label:"))
                    {
                        var idx = t.IndexOf("application-label:", StringComparison.Ordinal);
                        if (idx >= 0)
                        {
                            return t[(idx + "application-label:".Length)..].Trim();
                        }
                    }

                    // label='xxx'
                    var match = Regex.Match(t, "label='([^']*)'");
                    if (match.Success)
                        return match.Groups[1].Value;
                }

                return null;
            }

            private (int ExitCode, string Stdout, string Stderr) RunAdb(string arguments, bool capture)
            {
                var prefix = string.IsNullOrEmpty(Serial) ? "" : $"-s {Serial} ";
                return RunProcess("adb", prefix + arguments, capture);
            }
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
                var unityVersion = DetectUnityVersionFromContainers(zipArchives) ?? "Unknown";
                byte[]? metadataBytes = ExtractMetadataFromContainers(zipArchives);
                string rp = DetectRenderPipeline(metadataBytes);
                string entities = DetectEntities(metadataBytes);
                string addr = DetectAddressables(zips) ? "yes" : "no";
                var nsList = DetectMajorNamespaces(metadataBytes);

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
        
        static byte[]? ExtractMetadataFromContainers(List<ZipArchive> zips)
        {
            foreach (var z in zips)
            {
                var bytes = ExtractEntryBytes(z, MetadataPath);
                if (bytes != null && bytes.Length > 0) return bytes;
            }
            return null;
        }

        static string? DetectUnityVersionFromContainers(List<ZipArchive> zips)
        {
            // 1) globalgamemanagers
            foreach (var z in zips)
            {
                var v = FindVersionInEntry(z, "assets/bin/Data/globalgamemanagers");
                if (!string.IsNullOrEmpty(v)) return v;
            }

            // 2) data.unity3d
            foreach (var z in zips)
            {
                var v = FindVersionInEntry(z, "assets/bin/Data/data.unity3d");
                if (!string.IsNullOrEmpty(v)) return v;
            }

            // 3) libunity.so (arm64, v7a)
            foreach (var entryName in new[] { "lib/arm64-v8a/libunity.so", "lib/armeabi-v7a/libunity.so" })
            {
                foreach (var z in zips)
                {
                    var v = FindVersionInEntry(z, entryName);
                    if (!string.IsNullOrEmpty(v))  return v;
                }
            }

            // 4) fallback: metadata
            var metadataBytes = ExtractMetadataFromContainers(zips);
            if (metadataBytes != null)
            {
                var s = ExtractPrintableAscii(metadataBytes);
                var m = Regex.Match(s, VersionPattern);
                if (m.Success)
                    return m.Value;
            }

            return null;
        }

        static string? FindVersionInEntry(ZipArchive zip, string entryName)
        {
            var bytes = ExtractEntryBytes(zip, entryName);
            if (bytes == null || bytes.Length == 0)
                return null;

            var s = ExtractPrintableAscii(bytes);
            var m = Regex.Match(s, VersionPattern);
            return m.Success ? m.Value : null;
        }

        static string DetectRenderPipeline(byte[]? metadataBytes)
        {
            if (metadataBytes == null || metadataBytes.Length == 0)
                return "Unknown";

            var s = ExtractPrintableAscii(metadataBytes).ToLowerInvariant();

            if (s.Contains("unityengine.rendering.universal") ||
                s.Contains("universalrenderpipeline") ||
                s.Contains("forwardrenderer") ||
                s.Contains("renderer2d"))
                return "URP";

            if (s.Contains("unityengine.rendering.highdefinition") ||
                s.Contains("hdrenderpipeline"))
                return "HDRP";

            return "Built-in";
        }

        static string DetectEntities(byte[]? metadataBytes)
        {
            if (metadataBytes == null || metadataBytes.Length == 0)
                return "Unknown";

            var s = ExtractPrintableAscii(metadataBytes);

            bool has =
                s.IndexOf("Unity.Entities", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("EntityManager", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("EntityComponentStore", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("ComponentSystemGroup", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("HybridRenderer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("Unity.Physics", StringComparison.OrdinalIgnoreCase) >= 0;

            return has ? "yes" : "no";
        }

        static bool DetectAddressables(List<dynamic> zips)
        {
            foreach (var z in zips)
            {
                foreach (var entry in z.Zip.Entries)
                {
                    var name = entry.FullName.Replace("\\", "/").ToLowerInvariant();
                    if (name.Contains("aa/") ||
                        name.Contains("addressables") ||
                        Regex.IsMatch(name, @"catalog.*\.json", RegexOptions.IgnoreCase) ||
                        Regex.IsMatch(name, @"catalog.*\.hash", RegexOptions.IgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        static List<(string ns, int count)> DetectMajorNamespaces(byte[]? metadataBytes)
        {
            var result = new List<(string, int)>();

            if (metadataBytes == null || metadataBytes.Length == 0)
                return result;

            var s = ExtractPrintableAscii(metadataBytes);
            var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            var dict = new Dictionary<string, int>(StringComparer.Ordinal);
            var regex = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z0-9_.]+");

            foreach (var line in lines)
            {
                var m = regex.Match(line);
                if (!m.Success)
                    continue;

                var full = m.Value;
                var parts = full.Split('.');
                string key = parts.Length >= 2 ? parts[0] + "." + parts[1] : parts[0];

                dict.TryGetValue(key, out var cnt);
                dict[key] = cnt + 1;
            }

            foreach (var kv in dict.OrderByDescending(k => k.Value).Take(20))
                result.Add((kv.Key, kv.Value));

            return result;
        }

        static byte[]? ExtractEntryBytes(ZipArchive zip, string entryName)
        {
            ZipArchiveEntry? entry = null;
            foreach (var e in zip.Entries)
            {
                var name = e.FullName.Replace("\\", "/");
                if (string.Equals(name, entryName, StringComparison.OrdinalIgnoreCase))
                {
                    entry = e;
                    break;
                }
            }

            if (entry == null)
                return null;

            using var ms = new MemoryStream();
            using (var s = entry.Open())
                s.CopyTo(ms);

            return ms.ToArray();
        }

        static string ExtractPrintableAscii(byte[] bytes, int minLen = 4)
        {
            var sb = new StringBuilder(bytes.Length);
            var current = new StringBuilder();

            foreach (var b in bytes)
            {
                if (b >= 32 && b <= 126)
                {
                    current.Append((char)b);
                }
                else
                {
                    if (current.Length >= minLen)
                    {
                        sb.AppendLine(current.ToString());
                    }
                    current.Clear();
                }
            }

            if (current.Length >= minLen)
            {
                sb.AppendLine(current.ToString());
            }

            return sb.ToString();
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