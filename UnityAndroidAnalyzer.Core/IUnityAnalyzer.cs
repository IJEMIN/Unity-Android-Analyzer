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

public class DummyUnityAnalyzer : IUnityAnalyzer
{
    public Task<AnalysisResult> AnalyzeLocalAsync(string apkPath, IEnumerable<string> obbPaths)
    {
        var result = new AnalysisResult
        {
            Title = "Dummy Analysis",
            UnityVersion = "2024.1.0f1",
            RenderPipeline = "URP",
            EntitiesUsed = "Yes",
            AddressablesUsed = "No",
            HavokUsed = "No",
            MajorNamespaces = new List<(string Namespace, int Count)>
            {
                ("UnityEngine", 1500),
                ("System", 800),
                ("MyGameNamespace", 500)
            },
            Markdown = "# Dummy Analysis Result\n\nThis is a dummy analysis result."
        };

        return Task.FromResult(result);
    }

    public Task<AnalysisResult> AnalyzeDeviceAsync(string deviceSerial, string packageName)
    {
        var result = new AnalysisResult
        {
            Title = $"Dummy Device Analysis: {packageName}",
            UnityVersion = "2024.1.0f1",
            RenderPipeline = "URP",
            EntitiesUsed = "Yes",
            AddressablesUsed = "No",
            HavokUsed = "No",
            MajorNamespaces = new List<(string Namespace, int Count)>
            {
                ("UnityEngine", 1500),
                ("System", 800),
                ("MyGameNamespace", 500)
            },
            Markdown = $"# Dummy Device Analysis Result\n\nPackage: {packageName}\nDevice: {deviceSerial}"
        };

        return Task.FromResult(result);
    }
}