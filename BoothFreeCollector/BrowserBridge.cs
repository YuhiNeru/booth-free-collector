using Microsoft.Win32;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace BoothFreeCollector;

public static class BrowserBridge
{
    public const string ExtensionId = "ipocnbpbmnbjhliliadogibolpcmnjhd";
    public const string HostName = "pm.booth.free_collector";
    public static string PipeName => "BoothFreeCollector-" + Environment.UserName + (Environment.GetEnvironmentVariable("BOOTH_COLLECTOR_TEST_STATE") is string path ? "-test-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..12] : "");
    public static void Register()
    {
        if (Environment.GetEnvironmentVariable("BOOTH_COLLECTOR_TEST_STATE") is not null) return;
        string path = Path.Combine(Storage.Root, "native-host.json"); Directory.CreateDirectory(Storage.Root);
        File.WriteAllText(path, JsonSerializer.Serialize(new { name = HostName, description = "BOOTH Free Collector local file helper", path = Environment.ProcessPath, type = "stdio", allowed_origins = new[] { "chrome-extension://" + ExtensionId + "/" } }));
        foreach (string browser in new[] { "Google\\Chrome", "Microsoft\\Edge" })
        { using var key = Registry.CurrentUser.CreateSubKey(@"Software\" + browser + @"\NativeMessagingHosts\" + HostName); key.SetValue("", path); }
    }
    public static void Unregister(string? root = null)
    {
        if (Environment.GetEnvironmentVariable("BOOTH_COLLECTOR_TEST_STATE") is not null) return;
        string owned = Path.GetFullPath(Path.Combine(root ?? Storage.Root, "native-host.json"));
        foreach (string browser in new[] { "Google\\Chrome", "Microsoft\\Edge" })
        {
            string subkey = @"Software\" + browser + @"\NativeMessagingHosts\" + HostName;
            using var key = Registry.CurrentUser.OpenSubKey(subkey);
            if (key?.GetValue("") is string path && Path.GetFullPath(path).Equals(owned, StringComparison.OrdinalIgnoreCase))
                Registry.CurrentUser.DeleteSubKey(subkey, false);
        }
        if (File.Exists(owned)) File.Delete(owned);
    }
    public static async Task<byte[]?> Read(Stream input, int max, CancellationToken ct = default)
    {
        byte[] length = new byte[4]; int first = await input.ReadAsync(length.AsMemory(0, 1), ct); if (first == 0) return null;
        await input.ReadExactlyAsync(length.AsMemory(1, 3), ct); int size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size is <= 0 || size > max) throw new IOException("Bridge message too large");
        byte[] bytes = new byte[size]; await input.ReadExactlyAsync(bytes, ct); return bytes;
    }
    public static async Task Write(Stream output, byte[] bytes, CancellationToken ct = default)
    {
        if (bytes.Length > 950000) throw new IOException("Bridge reply too large");
        byte[] length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        await output.WriteAsync(length, ct); await output.WriteAsync(bytes, ct); await output.FlushAsync(ct);
    }
    public static async Task RunHost(string[] args)
    {
        if (!args.Contains("chrome-extension://" + ExtensionId + "/")) return;
        using var input = Console.OpenStandardInput(); using var output = Console.OpenStandardOutput();
        try
        {
            while (await Read(input, 13 * 1024 * 1024) is byte[] message)
            {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            byte[] reply;
            try
            {
                await pipe.ConnectAsync(1000, timeout.Token);
                // Input may be larger than native host replies (catalog uploads).
                byte[] size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, message.Length);
                await pipe.WriteAsync(size, timeout.Token); await pipe.WriteAsync(message, timeout.Token); await pipe.FlushAsync(timeout.Token);
                reply = await Read(pipe, 950000, timeout.Token) ?? throw new IOException("No response");
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
            { reply = JsonSerializer.SerializeToUtf8Bytes(new { desktop = false, reason = "Windows app is not running" }); }
            await Write(output, reply);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
    }
}

public sealed class BridgeServer(Func<JsonElement, Task<object>> handler) : IDisposable
{
    readonly CancellationTokenSource stop = new();
    public void Start() => _ = Serve();
    async Task Serve()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(BrowserBridge.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                byte[]? message = await BrowserBridge.Read(pipe, 13 * 1024 * 1024, timeout.Token); if (message is null) continue;
                object result;
                try { using var doc = JsonDocument.Parse(message); result = await handler(doc.RootElement.Clone()); }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or ArgumentException) { result = new { desktop = true, ok = false, error = ex.Message }; }
                await BrowserBridge.Write(pipe, JsonSerializer.SerializeToUtf8Bytes(result, Storage.Json), timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { }
        }
    }
    public void Dispose() { stop.Cancel(); }
}
