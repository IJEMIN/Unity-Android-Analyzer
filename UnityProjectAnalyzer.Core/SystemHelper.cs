using System.Diagnostics;
using System.Text;

namespace UnityProjectAnalyzer.Core;

public static class SystemHelper
{
    public static (int ExitCode, string Stdout, string Stderr) RunProcess(
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
}