using System.IO.Compression;
using System.Text;

namespace UnityAndroidAnalyzer.Core;

public class UnityAnalyzer : IUnityAnalyzer
{
    public string DownloadRootPath { get; set; } = 
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityAndroidAnalyzer");

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

        var tempDir = Path.Combine(DownloadRootPath, packageName);
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

            // 분석 수행
            var result = await AnalyzeLocalAsync(localApkPaths[0], localApkPaths.Skip(1).Concat(localObbPaths));
            
            // 임시 파일 경로 저장 (GUI에서 열기 기능을 위해)
            var metadataPath = Path.Combine(tempDir, "global-metadata.dat");
            var scriptingAssembliesPath = Path.Combine(tempDir, "ScriptingAssemblies.json");
            
            return result;
        }
        finally
        {
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
        var ngui = Analyzer.DetectNgui(scriptingAssembliesJson, metadataBytes);
        var addr = Analyzer.DetectAddressables(zipArchives) ? "Yes" : "No";
        var nsList = Analyzer.DetectMajorNamespaces(metadataBytes);
        var havok = Analyzer.DetectHavokPhysics(scriptingAssembliesJson, runtimeInitJson, metadataBytes);
        var uitk = Analyzer.DetectUiToolkit(zipArchives);

        // 추출된 파일들을 임시 디렉토리에 저장하여 나중에 열어볼 수 있게 함
        string? savedMetadataPath = null;
        string? savedScriptingPath = null;
        try
        {
            var tempDir = Path.Combine(DownloadRootPath, "LastAnalysis");
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);

            if (metadataBytes != null)
            {
                savedMetadataPath = Path.Combine(tempDir, "global-metadata.dat");
                File.WriteAllBytes(savedMetadataPath, metadataBytes);
            }

            if (!string.IsNullOrEmpty(scriptingAssembliesJson))
            {
                savedScriptingPath = Path.Combine(tempDir, "ScriptingAssemblies.json");
                File.WriteAllText(savedScriptingPath, scriptingAssembliesJson);
            }
        }
        catch { /* Ignore */ }

        var sb = new StringBuilder();
        sb.AppendLine($"## {title}");
        sb.AppendLine();
        sb.AppendLine($"- **Unity Version:** `{unityVersion}`");
        sb.AppendLine($"- **Render Pipeline:** `{rp}`");
        sb.AppendLine($"- **Entities Used:** `{entities}`");
        sb.AppendLine($"- **NGUI Used:** `{ngui}`");
        sb.AppendLine($"- **Addressables Used:** `{addr}`");
        sb.AppendLine($"- **Havok Used:** `{havok}`");
        sb.AppendLine($"- **UI Toolkit Used:** `{uitk}`");
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
            NguiUsed = ngui,
            AddressablesUsed = addr,
            HavokUsed = havok,
            UiToolkitUsed = uitk,
            MajorNamespaces = nsList,
            Markdown = sb.ToString(),
            MetadataPath = savedMetadataPath,
            ScriptingAssembliesPath = savedScriptingPath
        };
    }
}
