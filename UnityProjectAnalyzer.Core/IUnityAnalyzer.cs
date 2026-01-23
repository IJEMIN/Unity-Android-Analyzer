namespace UnityProjectAnalyzer.Core;

// UnityProjectAnalyzer.Core

public class AnalysisResult
{
    public string Title { get; set; } = "";
    public string UnityVersion { get; set; } = "";
    public string RenderPipeline { get; set; } = "";
    public string EntitiesUsed { get; set; } = "";
    public string NguiUsed { get; set; } = "";
    public string AddressablesUsed { get; set; } = "";
    public string HavokUsed { get; set; } = "";
    public string EntitiesPhysicsUsed { get; set; } = "";
    public string UiToolkitUsed { get; set; } = "";
    public List<(string Script, int Count)> MajorScriptInsights { get; set; } = new();
    
    // 파일 경로 저장을 위한 필드
    public string? MetadataPath { get; set; }
    public string? ScriptingAssembliesPath { get; set; }
}

public interface IUnityAnalyzer
{
    string DownloadRootPath { get; set; }
    Task<AnalysisResult> AnalyzeLocalAsync(string apkPath, IEnumerable<string> obbPaths);
    Task<AnalysisResult> AnalyzeDeviceAsync(string deviceSerial, string packageName);
}
