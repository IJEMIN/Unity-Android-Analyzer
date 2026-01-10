namespace UnityAndroidAnalyzer.Core;

// UnityAndroidAnalyzer.Core

public class AnalysisResult
{
    public string Title { get; set; } = "";
    public string UnityVersion { get; set; } = "";
    public string RenderPipeline { get; set; } = "";
    public string EntitiesUsed { get; set; } = "";
    public string AddressablesUsed { get; set; } = "";
    public string HavokUsed { get; set; } = "";
    public List<(string Namespace, int Count)> MajorNamespaces { get; set; } = new();
    public string Markdown { get; set; } = "";
}

public interface IUnityAnalyzer
{
    Task<AnalysisResult> AnalyzeLocalAsync(string apkPath, IEnumerable<string> obbPaths);
    Task<AnalysisResult> AnalyzeDeviceAsync(string deviceSerial, string packageName);
}
