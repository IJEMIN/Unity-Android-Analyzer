using System.Text.RegularExpressions;

namespace UnityProjectAnalyzer.Core;


public class AdbHelper
{
    public string? Serial { get; private set; }

    public bool SelectDevice()
    {
        var devices = GetDevices();

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

    public List<string> GetDevices()
    {
        var (exit, stdout, stderr) = SystemHelper.RunProcess("adb", "devices", true);
        var devices = new List<string>();
        if (exit != 0)
        {
            return devices;
        }

        var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("List of devices"))
                continue;

            var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1] == "device")
                devices.Add(parts[0]);
        }

        return devices;
    }

    public bool Connect(string address)
    {
        var (exit, stdout, stderr) = SystemHelper.RunProcess("adb", $"connect {address}", true);
        if (exit == 0 && stdout.Contains("connected to"))
        {
            // adb connect might not set Serial immediately if we want to use it, 
            // but usually the address becomes the serial.
            return true;
        }
        return false;
    }

    public void SetSerial(string serial)
    {
        Serial = serial;
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
        return SystemHelper.RunProcess("adb", prefix + arguments, capture);
    }

    public (int ExitCode, string Stdout, string Stderr) RunLogcat(string packageName, int durationMs = 5000)
    {
        // Start app
        var (exit, stdout, stderr) = RunAdb($"shell monkey -p {packageName} -c android.intent.category.LAUNCHER 1", true);
        if (exit != 0) return (exit, stdout, stderr);

        // Wait a bit for app to start and logs to be generated
        Thread.Sleep(1000);

        // Capture logcat (last 1000 lines or so for unity)
        return RunAdb("logcat -d", true);
    }
}