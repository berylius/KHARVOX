using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace KharvoxLauncher;

internal sealed class WineVulkanLoader : IDisposable
{
    private const string MarkerName = ".kharvox-wine-loader";
    private const string LoaderName = "vulkan-1.dll";
    private readonly string gameDirectory;
    private readonly string token;
    private readonly string hash;
    private readonly string logPath;
    private bool cleanupTransferred;

    private WineVulkanLoader(string gameDirectory, string token, string hash, string logPath)
    {
        this.gameDirectory = gameDirectory;
        this.token = token;
        this.hash = hash;
        this.logPath = logPath;
    }

    internal static WineVulkanLoader Prepare(string gameDirectory, string runtimeDirectory, string logPath)
    {
        var source = Path.Combine(runtimeDirectory, "wine-loader", LoaderName);
        if (!File.Exists(source))
            throw new FileNotFoundException(
                "Wine/Proton requires the KHARVOX Wine Vulkan loader. Extract the complete build, including the wine-loader folder.", source);

        Recover(gameDirectory, logPath);
        var destination = Path.Combine(gameDirectory, LoaderName);
        var marker = Path.Combine(gameDirectory, MarkerName);
        if (File.Exists(destination))
            throw new InvalidOperationException(
                "DOOM already has a vulkan-1.dll. KHARVOX will not overwrite it; remove the conflicting loader before launching under Wine/Proton.");

        var token = Guid.NewGuid().ToString("N");
        var hash = HashFile(source);
        var lease = new WineVulkanLoader(gameDirectory, token, hash, logPath);
        using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            writer.Write(token + "\n" + hash + "\n");
        var createdDestination = false;
        try
        {
            using var input = File.OpenRead(source);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            createdDestination = true;
            input.CopyTo(output);
        }
        catch (Exception copyError)
        {
            try
            {
                if (createdDestination) lease.Cleanup();
                else lease.RemoveMarker();
            }
            catch (Exception cleanupError) when (cleanupError is IOException || cleanupError is UnauthorizedAccessException
                || cleanupError is InvalidOperationException)
            {
                throw new AggregateException("Wine loader staging failed and its files could not be safely removed.", copyError, cleanupError);
            }
            throw;
        }
        return lease;
    }

    internal void Configure(ProcessStartInfo startInfo, string runtimeDirectory)
    {
        var wineDirectory = Path.Combine(runtimeDirectory, "wine-loader");
        var icd = Path.Combine(wineDirectory, "winevulkan.json");
        var json = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
        {
            ["file_format_version"] = "1.0.0",
            ["ICD"] = new Dictionary<string, string>
            {
                ["library_path"] = Path.Combine(Environment.SystemDirectory, "winevulkan.dll"),
                ["api_version"] = "1.3.0"
            }
        });
        File.WriteAllText(icd, json, new UTF8Encoding(false));
        var overrides = startInfo.EnvironmentVariables["WINEDLLOVERRIDES"];
        var preserved = new List<string>();
        foreach (var entry in (overrides ?? "").Split(';'))
        {
            var split = entry.IndexOf('=');
            if (split < 0) { if (entry.Length > 0) preserved.Add(entry); continue; }
            var names = entry.Substring(0, split).Split(',')
                .Where(name => !string.Equals(name.Trim(), "vulkan-1", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (names.Length > 0) preserved.Add(string.Join(",", names) + entry.Substring(split));
        }
        preserved.Add("vulkan-1=n");
        startInfo.EnvironmentVariables["WINEDLLOVERRIDES"] = string.Join(";", preserved);
        startInfo.EnvironmentVariables["KHARVOX_WINE_ICD"] = icd;
        startInfo.EnvironmentVariables["KHARVOX_WINE_LAYERS"] = runtimeDirectory;
        RemoveForcedKharvoxLayer(startInfo);
    }

    // Wine hands these variables to the host Vulkan loader as well, which then
    // looks for the Windows layer among Linux layers. The private loader finds
    // the layer through KHARVOX_WINE_LAYERS; a manual request only breaks DOOM.
    internal static bool RemoveForcedKharvoxLayer(ProcessStartInfo startInfo)
    {
        const string layer = "VK_LAYER_KHARVOX_OPENXR";
        var requested = startInfo.EnvironmentVariables["VK_INSTANCE_LAYERS"];
        if (requested is null) return false;
        var names = requested.Split(new[] { ';', ':', ',' }, StringSplitOptions.RemoveEmptyEntries);
        var others = names.Where(name => !string.Equals(name.Trim(), layer, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (others.Length == names.Length) return false;
        if (others.Length > 0)
        {
            startInfo.EnvironmentVariables["VK_INSTANCE_LAYERS"] = string.Join(
                requested.Contains(";") || !requested.Contains(":") ? ";" : ":", others);
            return true;
        }
        startInfo.EnvironmentVariables.Remove("VK_INSTANCE_LAYERS");
        startInfo.EnvironmentVariables.Remove("VK_LAYER_PATH");
        startInfo.EnvironmentVariables.Remove("VK_ADD_LAYER_PATH");
        return true;
    }

    internal void StartCleanupHelper(Process game)
    {
        var started = game.StartTime.ToUniversalTime().Ticks;
        var info = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = string.Join(" ", new[] {
                "--wine-loader-cleanup", gameDirectory, token, hash,
                game.Id.ToString(CultureInfo.InvariantCulture),
                started.ToString(CultureInfo.InvariantCulture), logPath
            }.Select(QuoteArgument))
        };
        using var helper = Process.Start(info)
            ?? throw new InvalidOperationException("Could not start the Wine Vulkan loader cleanup helper.");
        cleanupTransferred = true;
    }

    internal void PreserveForRecovery() => cleanupTransferred = true;

    public void Dispose()
    {
        if (!cleanupTransferred)
            Cleanup();
    }

    internal static int RunCleanup(string[] args)
    {
        if (args.Length != 7
            || !int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || !long.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
            return 1;
        var logPath = args[6];
        var lease = new WineVulkanLoader(args[1], args[2], args[3], logPath);
        try
        {
            try
            {
                using var game = Process.GetProcessById(pid);
                if (game.StartTime.ToUniversalTime().Ticks == ticks)
                {
                    game.WaitForExit();
                    Log(logPath, "game exited pid=" + pid.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (ArgumentException) { }
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { lease.Cleanup(); return 0; }
                catch (IOException) when (attempt < 29) { Thread.Sleep(100); }
            }
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException
            || exception is InvalidOperationException || exception is System.ComponentModel.Win32Exception)
        {
            Log(logPath, "cleanup failed error=" + exception.GetType().Name + " message=" + exception.Message);
        }
        return 1;
    }

    internal static void Recover(string gameDirectory, string logPath)
    {
        var marker = Path.Combine(gameDirectory, MarkerName);
        if (!File.Exists(marker)) return;
        var lines = File.ReadAllLines(marker);
        if (lines.Length != 2 || !Guid.TryParseExact(lines[0], "N", out _)
            || lines[1].Length != 64 || !lines[1].All(Uri.IsHexDigit))
            throw new InvalidOperationException("Invalid KHARVOX Wine loader marker; no files were removed.");
        new WineVulkanLoader(gameDirectory, lines[0], lines[1], logPath).Cleanup();
    }

    private void Cleanup()
    {
        var marker = Path.Combine(gameDirectory, MarkerName);
        if (!File.Exists(marker)) return;
        var lines = File.ReadAllLines(marker);
        if (lines.Length != 2 || lines[0] != token || lines[1] != hash)
            throw new InvalidOperationException("KHARVOX Wine loader marker changed; no files were removed.");
        var loader = Path.Combine(gameDirectory, LoaderName);
        if (File.Exists(loader))
        {
            if (!string.Equals(HashFile(loader), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The DOOM Vulkan loader changed; KHARVOX will not remove it.");
            File.Delete(loader);
        }
        RemoveMarker();
        Log(logPath, "temporary loader removed from \"" + gameDirectory + "\"");
    }

    private void RemoveMarker()
    {
        var marker = Path.Combine(gameDirectory, MarkerName);
        var lines = File.ReadAllLines(marker);
        if (lines.Length != 2 || lines[0] != token || lines[1] != hash)
            throw new InvalidOperationException("KHARVOX Wine loader marker changed; no files were removed.");
        File.Delete(marker);
    }

    private static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }

    private static void Log(string path, string message)
    {
        try { File.AppendAllText(path, "[KHARVOX][LAUNCHER] Wine Vulkan " + message + Environment.NewLine); }
        catch (IOException exception) { Trace.TraceError("Wine Vulkan log failure: " + exception); }
        catch (UnauthorizedAccessException exception) { Trace.TraceError("Wine Vulkan log failure: " + exception); }
    }
}
