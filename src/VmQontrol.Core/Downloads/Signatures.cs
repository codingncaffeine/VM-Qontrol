using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace VmQontrol.Core.Downloads;

/// <summary>A publisher whose signing keys VM Qontrol carries, and the files it signs.</summary>
public sealed record SigningPublisher(string Name, string KeyFile, IReadOnlyList<string> Fingerprints);

/// <summary>What a signature check found: checked by the publisher's key, or not checked because gpgv is missing.</summary>
public sealed record SignatureCheck(bool Checked, string Note);

/// <summary>
/// OpenPGP signatures from the publishers that sign their downloads, checked with GnuPG's gpgv (the verify-only tool apt
/// itself uses) against keys built into VM Qontrol (Downloads/Keys, each fingerprint pinned here and taken from the
/// publisher's own page). gpgv runs in an empty, throwaway home folder, so keys in this person's own keyring never count.
/// A signature counts only when gpgv calls it good AND the key that made it is one of that publisher's pinned keys.
/// Without gpgv (a tarball on a system that lacks it), the published checksum over HTTPS still decides and the log says
/// the signature was not checked.
/// </summary>
public static partial class Signatures
{
    /// <summary>Ubuntu's cloud image builder ("UEC Image Automatic Signing Key"), signs cloud-images.ubuntu.com's SHA256SUMS.</summary>
    public static readonly SigningPublisher UbuntuCloud = new("Ubuntu", "ubuntu-cloud.asc", ["D2EB44626FDDC30B513D5BB71A5D6C4C7DB87C81"]);

    /// <summary>"Ubuntu CD Image Automatic Signing Key (2012)", signs the installer discs' SHA256SUMS (all flavors).</summary>
    public static readonly SigningPublisher UbuntuDiscs = new("Ubuntu", "ubuntu-discs.asc", ["843938DF228D22F7B3742BC0D94AA3F0EFE21092"]);

    /// <summary>The Debian CD signing keys listed on https://www.debian.org/CD/verify.</summary>
    public static readonly SigningPublisher Debian = new("Debian", "debian-cd.asc",
        ["10460DAD76165AD81FBC0CE9988021A964E6EA7D", "DF9B9C49EAA9298432589D76DA87E80D6294BE9B", "F41D30342F3546695F65C66942468F4009EA8AC3"]);

    /// <summary>Fedora 43 to 46's primary keys, from https://fedoraproject.org/fedora.gpg (a newer release's key needs a newer VM Qontrol).</summary>
    public static readonly SigningPublisher Fedora = new("Fedora", "fedora.asc",
        ["C6E7F081CF80E13146676E88829B606631645531", "36F612DCF27F7D1A48A835E4DBFCF71C6D9F90A6", "4F50A6114CD5C6976A7F1179655A4B02F577861E", "D924B10D3E810DABDD8B56B596E7E91491211FCE"]);

    /// <summary>The key Alpine's cloud page (https://alpinelinux.org/cloud/) names for its cloud images.</summary>
    public static readonly SigningPublisher AlpineCloud = new("Alpine", "alpine-cloud.asc", ["F26ADFADBAE702EF7AF637459DA7EF23BFFCDF22"]);

    public static IReadOnlyList<SigningPublisher> All { get; } = [UbuntuCloud, UbuntuDiscs, Debian, Fedora, AlpineCloud];

    /// <summary>The gpgv program; tests point it at a missing file to see the unchecked path.</summary>
    internal static string? GpgvOverride { get; set; }

    public static string? FindGpgv()
    {
        if (GpgvOverride is { } o) return File.Exists(o) ? o : null;
        return (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, "gpgv")).FirstOrDefault(File.Exists);
    }

    /// <summary>A detached signature (<paramref name="signature"/>) over the whole of <paramref name="dataFile"/>. Throws when it does not check out.</summary>
    public static async Task<SignatureCheck> VerifyDetachedAsync(SigningPublisher publisher, byte[] signature, string dataFile, string what, CancellationToken ct = default)
    {
        if (FindGpgv() is not { } gpgv) return Unchecked(publisher, what);
        using var work = new WorkFolder();
        var sig = Path.Combine(work.Path, "signature");
        await File.WriteAllBytesAsync(sig, signature, ct).ConfigureAwait(false);
        var (status, _) = await RunAsync(gpgv, work, publisher, [sig, dataFile], output: null, ct).ConfigureAwait(false);
        return Judge(publisher, status, what);
    }

    /// <summary>A clear-signed text (Fedora's CHECKSUM files): checked, and only the SIGNED part is returned, never text around it.</summary>
    public static async Task<(SignatureCheck Check, string? SignedText)> VerifyClearsignedAsync(SigningPublisher publisher, byte[] clearsigned, string what, CancellationToken ct = default)
    {
        if (FindGpgv() is not { } gpgv) return (Unchecked(publisher, what), null);
        using var work = new WorkFolder();
        var input = Path.Combine(work.Path, "signed");
        var output = Path.Combine(work.Path, "content");
        await File.WriteAllBytesAsync(input, clearsigned, ct).ConfigureAwait(false);
        var (status, _) = await RunAsync(gpgv, work, publisher, [input], output, ct).ConfigureAwait(false);
        var check = Judge(publisher, status, what);
        return (check, await File.ReadAllTextAsync(output, ct).ConfigureAwait(false));
    }

    private static SignatureCheck Unchecked(SigningPublisher publisher, string what)
    {
        var note = $"{what}: {publisher.Name}'s signature was not checked (gpgv is not installed); its published checksum decided.";
        Log.Warn(note);
        return new SignatureCheck(false, note);
    }

    /// <summary>gpgv's status lines → the verdict. GOODSIG and a VALIDSIG whose primary key is pinned, and no bad signature.</summary>
    internal static SignatureCheck Judge(SigningPublisher publisher, IReadOnlyList<string> status, string what)
    {
        var good = status.Any(l => l.StartsWith("[GNUPG:] GOODSIG ", StringComparison.Ordinal));
        var bad = status.Any(l => l.StartsWith("[GNUPG:] BADSIG ", StringComparison.Ordinal));
        // VALIDSIG <fpr> <date> <ts> <expire> <ver> <reserved> <pk-algo> <hash-algo> <class> <primary-fpr>
        var signers = status.Where(l => l.StartsWith("[GNUPG:] VALIDSIG ", StringComparison.Ordinal))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length >= 12).Select(p => p[11].ToUpperInvariant()).ToList();
        var pinned = signers.FirstOrDefault(f => publisher.Fingerprints.Contains(f, StringComparer.Ordinal));
        if (good && !bad && pinned is not null)
        {
            Log.Info($"{what}: signature checked, made by {publisher.Name}'s key {pinned}.");
            return new SignatureCheck(true, $"Signed by {publisher.Name}");
        }

        Log.Warn($"{what}: signature refused. gpgv said: {string.Join(" | ", status)}");
        throw new DownloadFailedException([bad
            ? $"{what} carries a signature that does not match it. It may have been changed on the way or on the server, so it was not used."
            : $"{what} is not signed by {publisher.Name}'s key that VM Qontrol knows, so it was not used. If {publisher.Name} has started signing with a new key, a newer VM Qontrol will know it."]);
    }

    private static async Task<(IReadOnlyList<string> Status, int Code)> RunAsync(string gpgv, WorkFolder work, SigningPublisher publisher, IReadOnlyList<string> files, string? output, CancellationToken ct)
    {
        var keyring = Path.Combine(work.Path, "keyring.gpg");
        await File.WriteAllBytesAsync(keyring, Dearmor(KeyText(publisher)), ct).ConfigureAwait(false);
        var start = new ProcessStartInfo(gpgv) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // An empty home of its own: this person's trustedkeys never count, and nothing is written to ~/.gnupg.
        foreach (var a in (string[])["--homedir", work.Path, "--status-fd", "1", "--keyring", keyring]) start.ArgumentList.Add(a);
        if (output is not null) { start.ArgumentList.Add("--output"); start.ArgumentList.Add(output); }
        start.ArgumentList.Add("--");
        foreach (var f in files) start.ArgumentList.Add(f);
        using var p = Process.Start(start) ?? throw new DownloadFailedException(["gpgv could not be started to check the signature."]);
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        _ = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        var lines = (await stdout.ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (lines, p.ExitCode);
    }

    /// <summary>The publisher's armored keys as they ship in the assembly.</summary>
    internal static string KeyText(SigningPublisher publisher)
    {
        using var stream = typeof(Signatures).Assembly.GetManifestResourceStream("VmQontrol.Core.Downloads.Keys." + publisher.KeyFile)
                           ?? throw new InvalidOperationException($"The signing key {publisher.KeyFile} is missing from the build.");
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return reader.ReadToEnd();
    }

    /// <summary>ASCII armor → the binary keyring gpgv reads (every armored block, its checksum line dropped).</summary>
    internal static byte[] Dearmor(string armored)
    {
        var result = new List<byte>();
        foreach (Match block in ArmorBlock().Matches(armored))
        {
            var body = block.Groups[1].Value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('=')).ToList();
            // Armor headers ("Comment: …") end at the first blank line, which the split above removed: drop lines with ':'.
            result.AddRange(Convert.FromBase64String(string.Concat(body.Where(l => !l.Contains(':', StringComparison.Ordinal)))));
        }

        if (result.Count == 0) throw new InvalidOperationException("A signing key file holds no key.");
        return [.. result];
    }

    [GeneratedRegex(@"-----BEGIN PGP PUBLIC KEY BLOCK-----(.*?)-----END PGP PUBLIC KEY BLOCK-----", RegexOptions.Singleline)]
    private static partial Regex ArmorBlock();

    /// <summary>The hash a sums file lists for <paramref name="name"/>: "HASH *name", "HASH  name" or Fedora's "SHA256 (name) = HASH".</summary>
    public static string? HashFor(string sums, string name)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            if (BsdLine().Match(line) is { Success: true } bsd && bsd.Groups[2].Value == name) return bsd.Groups[3].Value.ToLowerInvariant();
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0].Length >= 64 && parts[0].All(Uri.IsHexDigit) && parts[1].TrimStart('*').Trim() == name) return parts[0].ToLowerInvariant();
        }

        return null;
    }

    [GeneratedRegex(@"^(SHA256|SHA512) \((.+)\) = ([0-9a-fA-F]{64,128})$")]
    private static partial Regex BsdLine();

    private sealed class WorkFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("vmqontrol-gpgv-").FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
