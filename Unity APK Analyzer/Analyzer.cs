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

    public static string DetectEntities(byte[]? metadataBytes)
    {
        if (metadataBytes == null || metadataBytes.Length == 0)
            return "Unknown";

        var s = Analyzer.ExtractPrintableAscii(metadataBytes);

        bool has =
            s.IndexOf("Unity.Entities", StringComparison.OrdinalIgnoreCase) >= 0 ||
            s.IndexOf("EntityManager", StringComparison.OrdinalIgnoreCase) >= 0 ||
            s.IndexOf("EntityComponentStore", StringComparison.OrdinalIgnoreCase) >= 0 ||
            s.IndexOf("ComponentSystemGroup", StringComparison.OrdinalIgnoreCase) >= 0 ||
            s.IndexOf("HybridRenderer", StringComparison.OrdinalIgnoreCase) >= 0 ||
            s.IndexOf("Unity.Physics", StringComparison.OrdinalIgnoreCase) >= 0;

        return has ? "yes" : "no";
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
            var parts = full.Split('.');
            string key = parts.Length >= 2 ? parts[0] + "." + parts[1] : parts[0];

            dict.TryGetValue(key, out var cnt);
            dict[key] = cnt + 1;
        }

        foreach (var kv in dict.OrderByDescending(k => k.Value).Take(20))
            result.Add((kv.Key, kv.Value));

        return result;
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