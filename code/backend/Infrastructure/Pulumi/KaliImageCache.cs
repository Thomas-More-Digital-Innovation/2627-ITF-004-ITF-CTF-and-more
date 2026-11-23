using System.Diagnostics;

namespace Infrastructure.Pulumi
{
    /// <summary>
    /// Kali publishes its cloud image only as a .tar.xz holding a raw disk, which Proxmox can't import
    /// from a URL. This downloads it once, extracts it and converts it to a compressed qcow2 that the
    /// Pulumi program uploads to Proxmox. Requires `tar`, `xz` and `qemu-img` (qemu-utils) on the backend host.
    /// </summary>
    public static class KaliImageCache
    {
        private static readonly SemaphoreSlim Lock = new(1, 1);

        public static string CacheDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "cyberkit", "images");

        public static async Task<string> EnsureAsync(string url, CancellationToken ct = default)
        {
            var archiveName = Path.GetFileName(new Uri(url).AbsolutePath);
            var baseName = archiveName.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) ? archiveName[..^".tar.xz".Length] : Path.GetFileNameWithoutExtension(archiveName);
            Directory.CreateDirectory(CacheDir);
            var qcow2 = Path.Combine(CacheDir, baseName + ".qcow2");

            await Lock.WaitAsync(ct);
            try
            {
                if (File.Exists(qcow2)) return qcow2;

                var work = Path.Combine(CacheDir, baseName + ".work");
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
                Directory.CreateDirectory(work);
                try
                {
                    var archive = Path.Combine(work, archiveName);
                    Console.WriteLine($"Downloading {url} ...");
                    using (var http = new HttpClient { Timeout = TimeSpan.FromHours(1) })
                    await using (var src = await http.GetStreamAsync(url, ct))
                    await using (var dst = File.Create(archive))
                    {
                        await src.CopyToAsync(dst, ct);
                    }

                    Console.WriteLine("Extracting Kali image ...");
                    await RunAsync("tar", ["-xJf", archive, "-C", work], ct);
                    File.Delete(archive);

                    var disk = Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories)
                        .FirstOrDefault(f => f.EndsWith(".raw") || f.EndsWith(".img") || f.EndsWith(".qcow2"))
                        ?? throw new InvalidOperationException($"No disk image (.raw/.img/.qcow2) found in {archiveName}.");

                    Console.WriteLine("Converting Kali image to qcow2 ...");
                    var tmp = qcow2 + ".tmp";
                    await RunAsync("qemu-img", ["convert", "-c", "-O", "qcow2", disk, tmp], ct);
                    File.Move(tmp, qcow2, overwrite: true);
                    return qcow2;
                }
                finally
                {
                    Directory.Delete(work, recursive: true);
                }
            }
            finally
            {
                Lock.Release();
            }
        }

        private static async Task RunAsync(string file, string[] args, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start '{file}'.");
            var stderr = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"'{file} {string.Join(' ', args)}' failed ({p.ExitCode}): {await stderr}");
        }
    }
}
