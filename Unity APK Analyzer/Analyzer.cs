using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityAndroidAnalyzer;

public class Analyzer
{
    // ---- Config / Patterns ----
    const string VersionPattern =
        @"((20[0-9]{2}|[5-9][0-9]{3})\.[0-9]+\.[0-9]+[fpab][0-9]*)";
    private const string MetadataPath = "assets/bin/Data/Managed/Metadata/global-metadata.dat";
    
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

        foreach (var kv in dict.OrderByDescending(k => k.Value).Take(20))
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
}