using System.IO.Compression;
using System.Text;

namespace UnityAndroidAnalyzer.Core;

public class UnityAnalyzer : IUnityAnalyzer
{
    public async Task<AnalysisResult> AnalyzeLocalAsync(string apkPath, IEnumerable<string> obbPaths)
    {
        var containerPaths = new List<string> { apkPath };
        containerPaths.AddRange(obbPaths);

        var zipArchives = new List<ZipArchive>();
        try
        {
            foreach (var containerPath in containerPaths)
            {
                if (File.Exists(containerPath))
                {
                    zipArchives.Add(ZipFile.OpenRead(containerPath));
                }
            }

            if (zipArchives.Count == 0)
            {
                throw new FileNotFoundException("No valid APK/OBB containers found.");
            }

            return await Task.Run(() => PerformAnalysis(Path.GetFileName(apkPath), zipArchives));
        }
        finally
        {
            foreach (var z in zipArchives)
            {
                z.Dispose();
            }
        }
    }

    public async Task<AnalysisResult> AnalyzeDeviceAsync(string deviceSerial, string packageName)
    {
        var adb = new AdbHelper();
        adb.SetSerial(deviceSerial);

        var tempDir = Path.Combine(Path.GetTempPath(), "UnityAndroidAnalyzer", packageName);
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, true);
        }
        Directory.CreateDirectory(tempDir);

        try
        {
            var apkPaths = adb.GetApkPaths(packageName);
            var obbPaths = adb.GetObbPaths(packageName);

            var localApkPaths = new List<string>();
            var localObbPaths = new List<string>();

            for (int i = 0; i < apkPaths.Count; i++)
            {
                var localPath = Path.Combine(tempDir, $"base_{i}.apk");
                adb.Pull(apkPaths[i], localPath);
                localApkPaths.Add(localPath);
            }

            for (int i = 0; i < obbPaths.Count; i++)
            {
                var localPath = Path.Combine(tempDir, Path.GetFileName(obbPaths[i]));
                adb.Pull(obbPaths[i], localPath);
                localObbPaths.Add(localPath);
            }

            if (localApkPaths.Count == 0)
            {
                throw new Exception("Failed to pull APK from device.");
            }

            // 첫 번째 APK를 메인으로 사용
            return await AnalyzeLocalAsync(localApkPaths[0], localApkPaths.Skip(1).Concat(localObbPaths));
        }
        finally
        {
            // 임시 파일 삭제는 분석이 끝난 후에 하는 것이 좋으나, 
            // AnalyzeLocalAsync가 ZipArchive를 닫을 때까지 기다려야 하므로 
            // 호출 측에서 처리하거나 적절한 시점에 삭제가 필요함.
            // 여기서는 일단 남겨두거나 명시적인 정리가 필요할 수 있음.
        }
    }

    private AnalysisResult PerformAnalysis(string title, List<ZipArchive> zipArchives)
    {
        var scriptingAssembliesJson = Analyzer.ExtractJsonTextFromContainers(zipArchives, "assets/bin/Data/ScriptingAssemblies.json");
        var runtimeInitJson = Analyzer.ExtractJsonTextFromContainers(zipArchives, "assets/bin/Data/RuntimeInitializeOnLoads.json");
        var unityVersion = Analyzer.DetectUnityVersionFromContainers(zipArchives) ?? "Unknown";
        var metadataBytes = Analyzer.ExtractMetadataFromContainers(zipArchives);
        var rp = Analyzer.DetectRenderPipeline(metadataBytes);
        var entities = Analyzer.DetectEntities(scriptingAssembliesJson, runtimeInitJson);
        var addr = Analyzer.DetectAddressables(zipArchives) ? "Yes" : "No";
        var nsList = Analyzer.DetectMajorNamespaces(metadataBytes);
        var havok = Analyzer.DetectHavokPhysics(scriptingAssembliesJson, runtimeInitJson, metadataBytes);

        var sb = new StringBuilder();
        sb.AppendLine($"## {title}");
        sb.AppendLine();
        sb.AppendLine($"- **Unity Version:** `{unityVersion}`");
        sb.AppendLine($"- **Render Pipeline:** `{rp}`");
        sb.AppendLine($"- **Entities Used:** `{entities}`");
        sb.AppendLine($"- **Addressables Used:** `{addr}`");
        sb.AppendLine($"- **Havok Used:** `{havok}`");
        sb.AppendLine();
        sb.AppendLine("### Major Namespaces (top 30)");
        sb.AppendLine();

        if (nsList.Count == 0)
        {
            sb.AppendLine("_No namespace information available (metadata not found or unreadable)._");
        }
        else
        {
            foreach (var (ns, count) in nsList)
            {
                sb.AppendLine($"- `{ns}` ({count})");
            }
        }

        return new AnalysisResult
        {
            Title = title,
            UnityVersion = unityVersion,
            RenderPipeline = rp,
            EntitiesUsed = entities,
            AddressablesUsed = addr,
            HavokUsed = havok,
            MajorNamespaces = nsList,
            Markdown = sb.ToString()
        };
    }
}
