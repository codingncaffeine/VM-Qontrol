using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VmQontrol.Core.Install;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Media;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Core.Downloads;

/// <summary>An official cloud image VM Qontrol can make a ready machine from (D39).</summary>
public sealed record CloudImage(string Id, string DisplayName, string OsInfoId, string Summary);

/// <summary>The image file chosen for a <see cref="CloudImage"/> today, and the checksum its publisher lists for it.</summary>
public sealed record ResolvedCloudImage(CloudImage Image, Uri File, string FileName, HashAlgorithmName Algorithm, string Checksum);

/// <summary>What the person chose for the new machine; <see cref="Password"/> may be empty when an SSH key is given.</summary>
public sealed record CloudChoices
{
    public required string Name { get; init; }
    public required string UserName { get; init; }
    public string Password { get; init; } = "";
    public IReadOnlyList<string> SshKeys { get; init; } = [];
    public long DiskGiB { get; init; } = 20;
    public long MemoryMiB { get; init; } = 2048;
    public int Cores { get; init; } = 2;
}

/// <summary>
/// Ready-made Linux machines in seconds, as multipass, Proxmox, Hyper-V Quick Create and UTM's gallery make them (D39):
/// the distribution's own cloud image, checked against the checksum it publishes, copied and grown to the chosen size,
/// with a cloud-init disc that makes the account (password and/or SSH key), names the machine, and installs the QEMU
/// guest agent, so its address, clean shutdown and SSH command work from the first boot. No installer runs.
/// Downloaded images are kept, so the next machine from the same image takes seconds.
/// </summary>
public static partial class CloudImages
{
    public static readonly IReadOnlyList<CloudImage> All =
    [
        new("ubuntu-26.04", "Ubuntu 26.04 LTS", "http://ubuntu.com/ubuntu/26.04", "Ubuntu Server, the newest long-term release"),
        new("ubuntu-24.04", "Ubuntu 24.04 LTS", "http://ubuntu.com/ubuntu/24.04", "Ubuntu Server, the previous long-term release"),
        new("debian-13", "Debian 13", "http://debian.org/debian/13", "Debian's generic cloud image"),
        new("debian-12", "Debian 12", "http://debian.org/debian/12", "Debian's generic cloud image, the previous release"),
        new("fedora", "Fedora Cloud", "http://fedoraproject.org/fedora/unknown", "Fedora's newest Cloud Base image"),
        new("alpine", "Alpine Linux", "http://alpinelinux.org/alpinelinux/unknown", "Alpine's newest cloud image, tiny and quick"),
    ];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public static string CacheFolder(QontrolPaths paths) => CacheOverride ?? Path.Combine(paths.Data, "cloud-images");

    /// <summary>Tests: one kept copy of each image across test profiles (scratch/images/cloud), not a download per run.</summary>
    internal static string? CacheOverride { get; set; }

    /// <summary>Finds today's file and its published checksum (the distributions' own servers, over HTTPS).</summary>
    public static async Task<ResolvedCloudImage> ResolveAsync(CloudImage image, CancellationToken ct = default)
    {
        try
        {
            switch (image.Id)
            {
                case "ubuntu-26.04" or "ubuntu-24.04":
                {
                    var v = image.Id[7..];
                    var dir = $"https://cloud-images.ubuntu.com/releases/{v}/release/";
                    var name = $"ubuntu-{v}-server-cloudimg-amd64.img";
                    return new(image, new Uri(dir + name), name, HashAlgorithmName.SHA256, SumsLine(await GetTextAsync(dir + "SHA256SUMS", ct).ConfigureAwait(false), name));
                }

                case "debian-13" or "debian-12":
                {
                    var (code, n) = image.Id == "debian-13" ? ("trixie", "13") : ("bookworm", "12");
                    var dir = $"https://cloud.debian.org/images/cloud/{code}/latest/";
                    var name = $"debian-{n}-genericcloud-amd64.qcow2";
                    return new(image, new Uri(dir + name), name, HashAlgorithmName.SHA512, SumsLine(await GetTextAsync(dir + "SHA512SUMS", ct).ConfigureAwait(false), name));
                }

                case "fedora":
                {
                    // Fedora's own list of every release image with its checksum (what Fedora Media Writer reads); the
                    // mirrors' directory listings differ in format and freshness, so they are not read.
                    var json = System.Text.Json.Nodes.JsonNode.Parse(await GetTextAsync("https://fedoraproject.org/releases.json", ct).ConfigureAwait(false)) as System.Text.Json.Nodes.JsonArray;
                    var newest = (json ?? []).OfType<System.Text.Json.Nodes.JsonObject>()
                        .Select(o => (Version: (string?)o["version"], Variant: (string?)o["variant"], Arch: (string?)o["arch"], Link: (string?)o["link"], Sha: (string?)o["sha256"]))
                        .Where(x => x is { Variant: "Cloud", Arch: "x86_64", Link: { } l, Sha.Length: 64 } && int.TryParse(x.Version, out _) && FedoraImage().IsMatch(Path.GetFileName(l)))
                        .OrderByDescending(x => int.Parse(x.Version!, System.Globalization.CultureInfo.InvariantCulture)).FirstOrDefault();
                    if (newest.Link is not { } link) throw new DownloadFailedException(["Fedora's release list names no Cloud Base image right now. Try again later."]);
                    return new(image, new Uri(link), Path.GetFileName(link), HashAlgorithmName.SHA256, newest.Sha!);
                }

                case "alpine":
                {
                    const string dir = "https://dl-cdn.alpinelinux.org/alpine/latest-stable/releases/cloud/";
                    var name = Links(await GetTextAsync(dir, ct).ConfigureAwait(false)).Select(l => AlpineImage().Match(l)).Where(m => m.Success)
                        .OrderByDescending(m => Version.Parse(m.Groups[1].Value)).ThenByDescending(m => int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))
                        .Select(m => m.Value).FirstOrDefault() ?? throw new DownloadFailedException(["Alpine's server lists no cloud image right now. Try again later."]);
                    return new(image, new Uri(dir + name), name, HashAlgorithmName.SHA512, SumsLine(await GetTextAsync(dir + name + ".sha512", ct).ConfigureAwait(false), name, alone: true));
                }

                default:
                    throw new DownloadFailedException([$"No cloud image is known as {image.Id}."]);
            }
        }
        catch (HttpRequestException ex)
        {
            throw new DownloadFailedException([$"{image.DisplayName}'s download server could not be reached ({ex.Message}). Check the connection and try again."]);
        }
    }

    /// <summary>The image on this computer, downloaded and checked once; a kept copy is checked again before use.</summary>
    public static async Task<string> GetAsync(QontrolPaths paths, ResolvedCloudImage r, IProgress<DownloadProgress> progress, CancellationToken ct = default)
    {
        var folder = CacheFolder(paths);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, r.FileName);
        if (File.Exists(target))
        {
            progress.Report(new DownloadProgress(0, null, "Checking the kept image…"));
            if (await MatchesAsync(target, r, ct).ConfigureAwait(false)) return target;
            File.Delete(target); // the publisher has a newer build under the same name
        }

        var partial = target + ".part";
        progress.Report(new DownloadProgress(0, null, $"Downloading {r.Image.DisplayName}…"));
        var size = await ResumableDownload.ToPartialAsync(Http, r.File, partial, progress, $"Downloading {r.Image.DisplayName}…", r.Image.DisplayName, ct).ConfigureAwait(false);
        progress.Report(new DownloadProgress(size ?? 0, size, "Checking the download against its published checksum…"));
        if (!await MatchesAsync(partial, r, ct).ConfigureAwait(false))
        {
            File.Delete(partial);
            throw new DownloadFailedException([$"The download does not match the checksum {r.Image.DisplayName} publishes, so it was deleted. Try again; if it happens again, the server or the connection is damaging it."]);
        }

        File.Move(partial, target, overwrite: true);
        Log.Info($"Cloud image downloaded and checked: {r.File} → {target}");
        return target;
    }

    /// <summary>Makes the machine: the image copied as its own disk and grown, the cloud-init disc, and its settings.</summary>
    public static async Task<(MachineConfig Machine, string Directory)> CreateAsync(string libraryRoot, CloudImage image, string imageFile, CloudChoices c, CancellationToken ct = default)
    {
        var dir = MachineFactory.FolderFor(libraryRoot, c.Name);
        Directory.CreateDirectory(dir);
        try
        {
            var disk = Path.Combine(dir, "disk0.qcow2");
            // A disk of its own (not linked to the kept image), so the image can be deleted or replaced at any time.
            await QemuImg.RunAsync(ct, "convert", "-q", "-O", "qcow2", imageFile, disk).ConfigureAwait(false);
            await QemuImg.RunAsync(ct, "resize", "-q", disk, $"{c.DiskGiB}G").ConfigureAwait(false);
            var account = LinuxEasyInstall.AccountName(c.UserName);
            IsoWriter.Write(Path.Combine(dir, SeedFile), "cidata", new Dictionary<string, byte[]>
            {
                ["meta-data"] = Encoding.UTF8.GetBytes($"instance-id: vmq-{Guid.NewGuid():N}\nlocal-hostname: {LinuxEasyInstall.HostName(c.Name)}\n"),
                ["user-data"] = Encoding.UTF8.GetBytes(UserData(c, account)),
            });
            var machine = new MachineConfig
            {
                Name = c.Name,
                Guest = new GuestConfig { Os = "linux", OsInfoId = image.OsInfoId, Account = account },
                MemoryMiB = c.MemoryMiB,
                Cpu = new CpuConfig { Cores = c.Cores },
                Firmware = new FirmwareConfig { Kind = FirmwareKind.Uefi },
                Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = c.DiskGiB }],
                Nics = [new NicConfig { SshForward = true }],
                Media = [new MediaConfig { Role = MediaRole.CloudInit, File = SeedFile }],
                Notes = $"Made from {image.DisplayName}'s cloud image ({Path.GetFileName(imageFile)}). Account: {account}.",
            };
            MachineStore.Save(machine, Path.Combine(dir, MachineStore.FileName));
            Log.Info($"{c.Name}: made from {Path.GetFileName(imageFile)} in {dir}");
            return (machine, dir);
        }
        catch
        {
            // Nothing half-made is left in the library.
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            throw;
        }
    }

    public const string SeedFile = "cloud-init.iso";

    /// <summary>
    /// The cloud-init answers: the account (sudo with its password, or without one when only a key is given, as multipass
    /// does), the keys, and the guest agent installed and started (Debian/Ubuntu/Fedora use systemd, Alpine OpenRC).
    /// </summary>
    public static string UserData(CloudChoices c, string account)
    {
        var lines = new List<string>
        {
            "#cloud-config",
            "users:",
            $"  - name: {account}",
            c.Password.Length > 0 ? "    sudo: \"ALL=(ALL) ALL\"" : "    sudo: \"ALL=(ALL) NOPASSWD:ALL\"",
            $"    lock_passwd: {(c.Password.Length > 0 ? "false" : "true")}",
        };
        if (c.Password.Length > 0) lines.Add($"    passwd: \"{Sha512Crypt.Hash(c.Password)}\"");
        if (c.SshKeys.Count > 0)
        {
            lines.Add("    ssh_authorized_keys:");
            lines.AddRange(c.SshKeys.Select(k => $"      - \"{k.Trim().Replace("\"", "", StringComparison.Ordinal)}\""));
        }

        lines.Add($"ssh_pwauth: {(c.Password.Length > 0 ? "true" : "false")}");
        lines.Add("packages: [qemu-guest-agent, sudo]");
        lines.Add("runcmd:");
        lines.Add("  - [sh, -c, 'systemctl enable --now qemu-guest-agent 2>/dev/null || { rc-update add qemu-guest-agent default; rc-service qemu-guest-agent start; }']");
        lines.Add($"  - [sh, -c, 'if [ -x /bin/bash ] && command -v usermod >/dev/null; then usermod -s /bin/bash {account}; fi']");
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>The public keys in ~/.ssh this person would sign in with (the .pub files), offered as the default.</summary>
    public static IReadOnlyList<string> LocalPublicKeys(string? home = null)
    {
        var ssh = Path.Combine(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        if (!Directory.Exists(ssh)) return [];
        return [.. Directory.EnumerateFiles(ssh, "*.pub").Order(StringComparer.Ordinal)
            .Select(f => { try { return File.ReadAllText(f).Trim(); } catch (IOException) { return ""; } })
            .Where(k => k.StartsWith("ssh-", StringComparison.Ordinal) || k.StartsWith("ecdsa-", StringComparison.Ordinal) || k.StartsWith("sk-", StringComparison.Ordinal))];
    }

    private static async Task<bool> MatchesAsync(string file, ResolvedCloudImage r, CancellationToken ct)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        using var hash = IncrementalHash.CreateHash(r.Algorithm);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) hash.AppendData(buffer, 0, read);
        return string.Equals(Convert.ToHexString(hash.GetHashAndReset()), r.Checksum, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>"HASH *name" or "HASH  name" lines (sha256sum / sha512sum); <paramref name="alone"/>: a file holding one hash.</summary>
    internal static string SumsLine(string sums, string name, bool alone = false)
    {
        foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*').Trim() == name || alone && parts.Length >= 1 && IsHex(parts[0])) return parts[0];
        }

        throw new DownloadFailedException([$"The published checksums do not list {name}."]);
    }

    private static bool IsHex(string s) => s.Length >= 64 && s.All(Uri.IsHexDigit);

    private static IEnumerable<string> Links(string html) => Href().Matches(html).Select(m => m.Groups[1].Value);

    [GeneratedRegex("href=\"([^\"?#]+)\"")]
    private static partial Regex Href();

    [GeneratedRegex(@"^Fedora-Cloud-Base-Generic-[0-9.-]+\.x86_64\.qcow2$")]
    private static partial Regex FedoraImage();

    [GeneratedRegex(@"^alpine-(\d+\.\d+\.\d+)-x86_64-cloudinit-r(\d+)\.qcow2$")]
    private static partial Regex AlpineImage();
}
