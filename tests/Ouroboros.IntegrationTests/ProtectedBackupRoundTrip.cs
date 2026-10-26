using System.Diagnostics;
using System.Security.Cryptography;

namespace Ouroboros.IntegrationTests;

internal static class ProtectedBackupRoundTrip
{
    internal static async Task<byte[]> RunAsync(byte[] dump)
    {
        var root = Path.Combine(Path.GetTempPath(), "ouroboros-backup-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        try
        {
            var source = Path.Combine(root, "source.dump");
            var key = Path.Combine(root, "backup.key");
            var managed = Path.Combine(root, "managed");
            var restored = Path.Combine(root, "restored.dump");
            await File.WriteAllBytesAsync(source, dump, TestContext.Current.CancellationToken);
            var secret = RandomNumberGenerator.GetBytes(32);
            try { await File.WriteAllBytesAsync(key, secret, TestContext.Current.CancellationToken); }
            finally { CryptographicOperations.ZeroMemory(secret); }
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            await RunToolAsync("Protect", source, managed, key, null);
            await RunToolAsync("Restore", Directory.GetFiles(managed, "email-intents-*.json").Single(), managed, key, restored);
            var result = await File.ReadAllBytesAsync(restored, TestContext.Current.CancellationToken);
            Assert.Equal(dump, result);
            return result;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task RunToolAsync(string mode, string source, string directory, string key, string? restored)
    {
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-File", RepositoryRoot.Resolve("scripts", "messaging", "protected-email-backup.ps1"),
            "-Mode", mode, "-SourceFile", source, "-Directory", directory, "-KeyPath", key, "-KeyId", "fixture-key" })
        { start.ArgumentList.Add(argument); }
        if (restored is not null) { start.ArgumentList.Add("-RestoreFile"); start.ArgumentList.Add(restored); }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        await Task.WhenAll(output, error);
        Assert.True(process.ExitCode == 0, "Backup cifrado/autenticado da fixture não concluído.");
    }
}
