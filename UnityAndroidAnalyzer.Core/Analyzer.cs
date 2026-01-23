using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace UnityAndroidAnalyzer.Core;

public class Analyzer
{
    // ---- Config / Patterns ----
    const string VersionPattern =
        @"((20[0-9]{2}|[5-9][0-9]{3})\.[0-9]+\.[0-9]+[fpab][0-9]*)";
    private const string MetadataPath = "assets/bin/Data/Managed/Metadata/global-metadata.dat";
    
    
    public static string DetectHavokPhysics(
        string scriptingAssembliesJson,
        string runtimeInitJson,
        byte[]? metadataBytes)
    {
        bool hasHavokAssembly = false;
        if (!string.IsNullOrEmpty(scriptingAssembliesJson))
        {
            var s = scriptingAssembliesJson;
            if (s.IndexOf("Havok.Physics", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("com.havok.physics", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hasHavokAssembly = true;
            }
        }

        bool hasHavokRuntime = false;
        if (!string.IsNullOrEmpty(runtimeInitJson))
        {
            var s = runtimeInitJson;
            if (s.IndexOf("Havok.Physics", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hasHavokRuntime = true;
            }
        }

        bool hasHavokInMetadata = false;
        if (metadataBytes != null && metadataBytes.Length > 0)
        {
            var s = ExtractPrintableAscii(metadataBytes);
            if (s.IndexOf("Havok.Physics", StringComparison.OrdinalIgnoreCase) >= 0)
                hasHavokInMetadata = true;
        }

        // 최소 한 군데 이상 등장하면 사용한다고 본다
        if (hasHavokAssembly || hasHavokRuntime || hasHavokInMetadata)
            return "yes";

        return "no";
    }
    
    public static string DetectRenderPipeline(byte[]? metadataBytes)
    {
        if (metadataBytes == null || metadataBytes.Length == 0)
            return "Unknown";

        var s = ExtractPrintableAscii(metadataBytes).ToLowerInvariant();

        if (s.Contains("com.unity.render-pipelines.universal") || // package
            s.Contains("unityengine.rendering.universal") || // namespace
            s.Contains("universalrenderpipeline") ||
            s.Contains("forwardrenderer") ||
            s.Contains("renderer2d"))
            return "URP";

        if (s.Contains("com.unity.render-pipelines.high-definition") || // package
            s.Contains("unityengine.rendering.highdefinition") || // namespace
            s.Contains("hdrenderpipeline"))
            return "HDRP";
        
        // Scriptable Render Pipeline (SRP) without URP/HDRP 
        if (s.Contains("com.unity.render-pipelines.core")) return "SRP";

        return "Built-in";
    }

    public static string ExtractPrintableAscii(byte[] bytes, int minLen = 4)
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

    public static byte[]? ExtractMetadataFromContainers(List<ZipArchive> zips)
    {
        foreach (var z in zips)
        {
            var bytes = ExtractEntryBytes(z, MetadataPath);
            if (bytes != null && bytes.Length > 0) return bytes;
        }
        return null;
    }

    public static string? DetectUnityVersionFromContainers(List<ZipArchive> zips)
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
            var s = Analyzer.ExtractPrintableAscii(metadataBytes);
            var m = Regex.Match(s, VersionPattern);
            if (m.Success)
                return m.Value;
        }

        return null;
    }

    private static string? FindVersionInEntry(ZipArchive zip, string entryName)
    {
        var bytes = ExtractEntryBytes(zip, entryName);
        if (bytes == null || bytes.Length == 0)
            return null;

        var s = ExtractPrintableAscii(bytes);
        var m = Regex.Match(s, VersionPattern);
        return m.Success ? m.Value : null;
    }

    public static string DetectEntities(string scriptingAssembliesJson, string runtimeInitJson)
    {
        // 기준 1: ScriptingAssemblies.json에 Entities 관련 어셈블리 있는지
        bool hasEntitiesAssembly = false;
        if (!string.IsNullOrEmpty(scriptingAssembliesJson))
        {
            var s = scriptingAssembliesJson;
            if (s.IndexOf("Unity.Entities", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("Unity.Entities.Hybrid", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hasEntitiesAssembly = true;
            }
        }

        // 기준 2: RuntimeInitializeOnLoads.json에 Entities 관련 타입/어셈블리 초기화 항목이 있는지
        bool hasEntitiesRuntime = false;
        if (!string.IsNullOrEmpty(runtimeInitJson))
        {
            var s = runtimeInitJson;
            if (s.IndexOf("Unity.Entities", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("Unity.Entities.Hybrid", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hasEntitiesRuntime = true;
            }
        }
        
        if (hasEntitiesAssembly || hasEntitiesRuntime)
            return "yes";
        
        return "no";
    }

    public static string DetectNgui(string scriptingAssembliesJson, byte[]? metadataBytes)
    {
        bool hasNguiAssembly = false;
        if (!string.IsNullOrEmpty(scriptingAssembliesJson))
        {
            if (scriptingAssembliesJson.IndexOf("NGUI", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hasNguiAssembly = true;
            }
        }

        bool hasNguiInMetadata = false;
        if (metadataBytes != null && metadataBytes.Length > 0)
        {
            var s = ExtractPrintableAscii(metadataBytes);
            if (s.IndexOf("NGUI", StringComparison.OrdinalIgnoreCase) >= 0)
                hasNguiInMetadata = true;
        }

        if (hasNguiAssembly || hasNguiInMetadata)
            return "yes";

        return "no";
    }

    public static bool DetectAddressables(List<ZipArchive> zips)
    {
        foreach (var z in zips)
        {
            foreach (var entry in z.Entries)
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
    
    public static string ExtractJsonTextFromContainers(List<ZipArchive> zips, string entryPath)
    {
        foreach (var zip in zips)
        {
            var bytes = ExtractEntryBytes(zip, entryPath);
            if (bytes == null || bytes.Length <= 0) continue;
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                // fallback: try default encoding
                return Encoding.Default.GetString(bytes);
            }
        }

        return string.Empty;
    }
    
    public static List<(string ns, int count)> DetectMajorNamespaces(byte[]? metadataBytes)
    {
        var result = new List<(string, int)>();

        if (metadataBytes == null || metadataBytes.Length == 0)
            return result;

        var s = Analyzer.ExtractPrintableAscii(metadataBytes);
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var dict = new Dictionary<string, int>(StringComparer.Ordinal);
        var regex = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z0-9_.]+");

        foreach (var line in lines)
        {
            var m = regex.Match(line);
            if (!m.Success)
                continue;
            
            var full = m.Value;
            full = NormalizeNamespace(full);
            var parts = full.Split('.');
            string key = parts.Length >= 2 ? parts[0] + "." + parts[1] : parts[0];

            dict.TryGetValue(key, out var cnt);
            dict[key] = cnt + 1;
        }

        foreach (var kv in dict.OrderByDescending(k => k.Value).Take(30))
            result.Add((kv.Key, kv.Value));

        return result;
    }
    
    static string NormalizeNamespace(string ns)
    {
        // BUnityEngine..., HUnity.InternalAPI..., 등 한 글자 + Unity... 패턴을 Unity...로 정규화
        if (ns.Length > 6 &&
            char.IsUpper(ns[0]) &&
            ns.Substring(1).StartsWith("Unity", StringComparison.Ordinal))
        {
            return ns.Substring(1);
        }

        return ns;
    }

    private static byte[]? ExtractEntryBytes(ZipArchive zip, string entryName)
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

    public static void AnalyzeDataUnity3D(List<ZipArchive> zips)
    {
        foreach (var zip in zips)
        {
            var data = ExtractEntryBytes(zip, "assets/bin/Data/data.unity3d");
            if (data != null)
            {
                Console.WriteLine("[Analyzer] Found data.unity3d. Size: " + data.Length);
                using var ms = new MemoryStream(data);
                var reader = new UnityFsReader(ms);
                reader.Read();
            }

            // Also check for level0
            var level0 = ExtractEntryBytes(zip, "assets/bin/Data/level0");
            if (level0 != null)
            {
                Console.WriteLine("[Analyzer] Found level0. Size: " + level0.Length);
                using var ms = new MemoryStream(level0);
                var reader = new UnityAssetsReader(ms);
                reader.Read();
            }
        }
    }

    public static string DetectUiToolkit(List<ZipArchive> zips)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "UnityAndroidAnalyzer", "UiToolkitAnalysis_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "analysis.db");

        try
        {
            bool foundDataFiles = false;
            foreach (var zip in zips)
            {
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.StartsWith("assets/bin/Data/") && 
                        (entry.FullName.EndsWith(".assets") || 
                         entry.FullName.EndsWith(".sharedassets") || 
                         entry.FullName.Contains("globalgamemanagers")))
                    {
                        var targetPath = Path.Combine(tempDir, Path.GetFileName(entry.FullName));
                        entry.ExtractToFile(targetPath, true);
                        foundDataFiles = true;
                    }
                }
            }

            if (!foundDataFiles) return "no (no data files)";

            var startInfo = new ProcessStartInfo
            {
                FileName = "UnityDataTool",
                Arguments = $"analyze \"{tempDir}\" -o \"{dbPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return "error (UnityDataTool not found)";
            process.WaitForExit();

            if (!File.Exists(dbPath)) return "no (analysis.db not created)";

            bool hasUiToolkit = false;
            try
            {
                var queryStartInfo = new ProcessStartInfo
                {
                    FileName = "sqlite3",
                    Arguments = $"\"{dbPath}\" \"SELECT COUNT(*) FROM types WHERE name IN ('PanelSettings', 'UIDocument', 'PanelRenderer');\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var queryProcess = Process.Start(queryStartInfo);
                if (queryProcess != null)
                {
                    var output = queryProcess.StandardOutput.ReadToEnd().Trim();
                    queryProcess.WaitForExit();
                    if (int.TryParse(output, out int count) && count > 0)
                    {
                        hasUiToolkit = true;
                    }
                }
            }
            catch
            {
                return "error (sqlite3 query failed)";
            }

            return hasUiToolkit ? "yes" : "no";
        }
        catch (Exception ex)
        {
            return $"error ({ex.Message})";
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch { /* Ignore cleanup errors */ }
        }
    }
}