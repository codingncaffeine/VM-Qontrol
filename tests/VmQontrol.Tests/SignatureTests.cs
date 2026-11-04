using VmQontrol.Core.Downloads;

namespace VmQontrol.Tests;

/// <summary>
/// Publisher signatures on downloads (Signatures.cs). The fixtures are the publishers' own files, as served on
/// 2026-11-03: Ubuntu's cloud and disc SHA256SUMS with their .gpg, Debian 13.7's live SHA512SUMS with its .sign, and
/// Fedora 45's clear-signed Cloud CHECKSUM. Every test here is in one class: GpgvOverride is shared state.
/// </summary>
public sealed class SignatureTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "signatures", name);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool NoGpgv => Signatures.FindGpgv() is null;

    public static TheoryData<string, string, string> Detached => new()
    {
        { "ubuntu-cloud", "ubuntu-cloud-SHA256SUMS", "ubuntu-cloud-SHA256SUMS.gpg" },
        { "ubuntu-discs", "ubuntu-disc-SHA256SUMS", "ubuntu-disc-SHA256SUMS.gpg" },
        { "debian-cd", "debian-SHA512SUMS", "debian-SHA512SUMS.sign" },
    };

    private static SigningPublisher Publisher(string key) => Signatures.All.Single(p => p.KeyFile == key + ".asc");

    [Theory]
    [MemberData(nameof(Detached))]
    public async Task A_real_signed_checksum_list_checks_out_with_its_publishers_built_in_key(string key, string data, string signature)
    {
        Assert.SkipWhen(NoGpgv, "gpgv is not installed");
        var check = await Signatures.VerifyDetachedAsync(Publisher(key), await File.ReadAllBytesAsync(Fixture(signature), Ct), Fixture(data), "test", Ct);
        Assert.True(check.Checked);
    }

    [Theory]
    [MemberData(nameof(Detached))]
    public async Task One_changed_byte_in_a_signed_list_is_refused(string key, string data, string signature)
    {
        Assert.SkipWhen(NoGpgv, "gpgv is not installed");
        using var profile = new TestProfile("sig-tamper");
        var bytes = await File.ReadAllBytesAsync(Fixture(data), Ct);
        bytes[10] = (byte)(bytes[10] == (byte)'0' ? '1' : '0'); // a hash digit
        var changed = Path.Combine(profile.Root, data);
        await File.WriteAllBytesAsync(changed, bytes, Ct);
        var ex = await Assert.ThrowsAsync<DownloadFailedException>(() => Signatures.VerifyDetachedAsync(Publisher(key), File.ReadAllBytes(Fixture(signature)), changed, "test", Ct));
        Assert.Contains("does not match", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_good_signature_by_another_publishers_key_is_refused()
    {
        Assert.SkipWhen(NoGpgv, "gpgv is not installed");
        // Ubuntu's genuine signature, but checked as Debian's: Debian's keyring cannot vouch for it.
        await Assert.ThrowsAsync<DownloadFailedException>(() => Signatures.VerifyDetachedAsync(Signatures.Debian,
            File.ReadAllBytes(Fixture("ubuntu-disc-SHA256SUMS.gpg")), Fixture("ubuntu-disc-SHA256SUMS"), "test", Ct));
    }

    [Fact]
    public async Task A_clear_signed_list_gives_only_its_signed_text()
    {
        Assert.SkipWhen(NoGpgv, "gpgv is not installed");
        // A line put in front of the signed block (as a damaged mirror could) must never be read as a checksum.
        var planted = "SHA256 (Fedora-Cloud-Base-Generic-45-1.4.x86_64.qcow2) = " + new string('0', 64) + "\n";
        var bytes = System.Text.Encoding.ASCII.GetBytes(planted).Concat(await File.ReadAllBytesAsync(Fixture("fedora-CHECKSUM"), Ct)).ToArray();
        var (check, text) = await Signatures.VerifyClearsignedAsync(Signatures.Fedora, bytes, "test", Ct);
        Assert.True(check.Checked);
        Assert.DoesNotContain(new string('0', 64), text, StringComparison.Ordinal);
        Assert.Equal("50bdc5e70f6a24f540932d15411c1c6ae9fcfa40fb990d876d83c5cc823b2ad0", Signatures.HashFor(text!, "Fedora-Cloud-Base-Generic-45-1.4.x86_64.qcow2"));
        // Control: the planted line WOULD be found if the whole file were read.
        Assert.Equal(new string('0', 64), Signatures.HashFor(System.Text.Encoding.ASCII.GetString(bytes), "Fedora-Cloud-Base-Generic-45-1.4.x86_64.qcow2"));
    }

    [Fact]
    public async Task A_changed_clear_signed_list_is_refused()
    {
        Assert.SkipWhen(NoGpgv, "gpgv is not installed");
        var text = await File.ReadAllTextAsync(Fixture("fedora-CHECKSUM"), Ct);
        var changed = text.Replace("50bdc5e70f6a24f5", "50bdc5e70f6a24f6", StringComparison.Ordinal);
        Assert.NotEqual(text, changed);
        await Assert.ThrowsAsync<DownloadFailedException>(() => Signatures.VerifyClearsignedAsync(Signatures.Fedora, System.Text.Encoding.ASCII.GetBytes(changed), "test", Ct));
    }

    [Fact]
    public async Task Without_gpgv_the_signature_is_reported_unchecked_never_passed()
    {
        Signatures.GpgvOverride = "/nonexistent/gpgv";
        try
        {
            var check = await Signatures.VerifyDetachedAsync(Signatures.UbuntuDiscs, [1, 2, 3], Fixture("ubuntu-disc-SHA256SUMS"), "test", Ct);
            Assert.False(check.Checked);
            Assert.Contains("not checked", check.Note, StringComparison.Ordinal);
        }
        finally
        {
            Signatures.GpgvOverride = null;
        }
    }

    [Fact]
    public void Only_a_good_signature_from_a_pinned_key_counts()
    {
        const string Pinned = "843938DF228D22F7B3742BC0D94AA3F0EFE21092";
        string Valid(string fpr) => $"[GNUPG:] VALIDSIG {fpr} 2026-09-15 1789499472 0 4 0 1 10 00 {fpr}";
        Assert.True(Signatures.Judge(Signatures.UbuntuDiscs, ["[GNUPG:] GOODSIG D94AA3F0EFE21092 Ubuntu", Valid(Pinned)], "t").Checked);
        // A valid signature from a key that is not pinned (it would have to be in the keyring, but the rule holds anyway).
        Assert.Throws<DownloadFailedException>(() => Signatures.Judge(Signatures.UbuntuDiscs, ["[GNUPG:] GOODSIG 1A5D6C4C7DB87C81 x", Valid("D2EB44626FDDC30B513D5BB71A5D6C4C7DB87C81")], "t"));
        Assert.Throws<DownloadFailedException>(() => Signatures.Judge(Signatures.UbuntuDiscs, ["[GNUPG:] BADSIG D94AA3F0EFE21092 x", "[GNUPG:] GOODSIG D94AA3F0EFE21092 x", Valid(Pinned)], "t"));
        Assert.Throws<DownloadFailedException>(() => Signatures.Judge(Signatures.UbuntuDiscs, [Valid(Pinned)], "t")); // no GOODSIG (expired or revoked key)
        Assert.Throws<DownloadFailedException>(() => Signatures.Judge(Signatures.UbuntuDiscs, [], "t"));
    }

    [Fact]
    public void Every_publishers_key_file_ships_and_dearmors()
    {
        foreach (var p in Signatures.All)
        {
            var binary = Signatures.Dearmor(Signatures.KeyText(p));
            Assert.True(binary.Length > 500, p.KeyFile);
            Assert.True((binary[0] & 0xFC) == 0x98 || binary[0] == 0xC6, p.KeyFile); // an OpenPGP public-key packet (tag 6, old or new format)
            Assert.NotEmpty(p.Fingerprints);
        }
    }

    [Fact]
    public void Checksum_lines_are_read_in_all_three_formats()
    {
        var a = new string('a', 64);
        Assert.Equal(a, Signatures.HashFor($"{new string('b', 64)} *other.iso\n{a} *ubuntu-24.04.5-desktop-amd64.iso\n", "ubuntu-24.04.5-desktop-amd64.iso"));
        var b = new string('b', 128);
        Assert.Equal(b, Signatures.HashFor($"{b}  debian-live-13.7.0-amd64-standard.iso\n", "debian-live-13.7.0-amd64-standard.iso"));
        Assert.Equal(a, Signatures.HashFor($"# x\nSHA256 (Fedora-Server-dvd-x86_64-44-1.7.iso) = {a.ToUpperInvariant()}\n", "Fedora-Server-dvd-x86_64-44-1.7.iso"));
        Assert.Null(Signatures.HashFor($"{a} *other.iso\n", "mine.iso"));
    }

    [Fact]
    public void Ubuntu_flavors_Debian_and_Fedora_discs_are_checked_and_others_are_left_to_quickget()
    {
        Assert.Same(Signatures.UbuntuDiscs, DownloadJob.PublisherFor("kubuntu"));
        Assert.Same(Signatures.UbuntuDiscs, DownloadJob.PublisherFor("ubuntu-server"));
        Assert.Same(Signatures.Debian, DownloadJob.PublisherFor("debian"));
        Assert.Same(Signatures.Fedora, DownloadJob.PublisherFor("fedora"));
        Assert.Null(DownloadJob.PublisherFor("archlinux"));
        Assert.Null(DownloadJob.PublisherFor("macos"));
    }
    /// <summary>
    /// The real thing, set VMQONTROL_SIGNATURE_PROOF=1 (network; hashes about 6 GB of kept discs): today's six cloud
    /// images resolved from their publishers (Ubuntu and Fedora lists signed, Alpine's image signature checked on the kept
    /// copy, Debian unsigned), and the kept Ubuntu, Debian and Fedora installer discs in scratch/images checked against the
    /// signed lists where quickget downloads them, with a control: a disc under another disc's name is refused.
    /// </summary>
    [Trait("Tier", "Guest")]
    [Fact]
    public async Task Real_downloads_check_out_against_their_publishers_signatures()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("VMQONTROL_SIGNATURE_PROOF") != "1", "set VMQONTROL_SIGNATURE_PROOF=1");
        Assert.SkipWhen(NoGpgv, "gpgv is not installed");
        var images = Path.Combine(TestProfile.ScratchRoot, "images");
        Assert.True(Directory.Exists(images), images);
        var output = TestContext.Current.TestOutputHelper!;

        foreach (var image in CloudImages.All)
        {
            var r = await CloudImages.ResolveAsync(image, Ct);
            output.WriteLine($"{image.Id}: {r.FileName} signed-list={r.Signed} image-signature={(r.ImageSignature is null ? "none" : "yes")}");
            Assert.Equal(image.Id is "ubuntu-26.04" or "ubuntu-24.04" or "fedora", r.Signed);
            Assert.Equal(image.Id == "alpine", r.ImageSignature is not null);
            if (r.ImageSignature is { } sig && Path.Combine(images, "cloud", "alpine", r.FileName) is var kept && File.Exists(kept))
            {
                Assert.True((await Signatures.VerifyDetachedAsync(Signatures.AlpineCloud, sig, kept, "Alpine's image", Ct)).Checked);
                output.WriteLine($"alpine: kept {r.FileName} signature checked");
            }
        }

        var progress = new Progress<DownloadProgress>(_ => { });
        (string Os, string Release, string Url, string File)[] discs =
        [
            ("ubuntu-server", "24.04", "https://releases.ubuntu.com/24.04/ubuntu-24.04.5-live-server-amd64.iso", "ubuntu-24.04.5-live-server-amd64.iso"),
            ("debian", "13.7.0", "https://cdimage.debian.org/cdimage/archive/13.7.0/amd64/iso-cd/debian-13.7.0-amd64-netinst.iso", "debian-13.7.0-amd64-netinst.iso"),
            ("fedora", "44", "https://dl.fedoraproject.org/pub/fedora/linux/releases/44/Server/x86_64/iso/Fedora-Server-netinst-x86_64-44-1.7.iso", "Fedora-Server-netinst-x86_64-44-1.7.iso"),
        ];
        foreach (var d in discs)
        {
            var file = Path.Combine(images, d.File);
            Assert.True(File.Exists(file), file);
            var url = d.Url;
            if (d.Os == "debian")
            {
                // The current release lives under debian-cd/, an older one under cdimage/archive/.
                using var http = new HttpClient();
                using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), Ct);
                if (!head.IsSuccessStatusCode) url = "https://cdimage.debian.org/debian-cd/13.7.0/amd64/iso-cd/" + d.File;
            }

            await DownloadJob.CheckDiscSignatureAsync(new CatalogEntry(d.Os, d.Os, d.Release, "", ""), [url], file, progress, Ct);
            output.WriteLine($"{d.Os}: {d.File} matches its signed checksum ({url})");
        }

        // Control: the Debian disc presented as the Fedora one is refused by the signed list.
        var wrong = await Assert.ThrowsAsync<DownloadFailedException>(() => DownloadJob.CheckDiscSignatureAsync(
            new CatalogEntry("fedora", "fedora", "44", "", ""), [discs[2].Url], Path.Combine(images, discs[1].File), progress, Ct));
        Assert.Contains("does not match", wrong.Message, StringComparison.Ordinal);
    }
    /// <summary>The wizard's own download path for a signed system (Debian 12 netinst, about 650 MB through quickget):
    /// set VMQONTROL_SIGNATURE_DOWNLOAD=1. The signature step runs and the machine is made.</summary>
    [Trait("Tier", "Guest")]
    [Fact]
    public async Task A_real_quickget_download_of_a_signed_system_is_checked_against_the_signature()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("VMQONTROL_SIGNATURE_DOWNLOAD") != "1", "set VMQONTROL_SIGNATURE_DOWNLOAD=1");
        Assert.SkipWhen(NoGpgv || !File.Exists("/usr/bin/quickget"), "gpgv or quickget is not installed");
        using var profile = new TestProfile("signed-download");
        Directory.CreateDirectory(profile.Paths.DefaultLibrary);
        var reports = new List<DownloadProgress>();
        var job = new DownloadJob(new CatalogEntry("Debian", "debian", "12.15.0", "netinst", ""), profile.Paths.DefaultLibrary, "Debian 12");
        var (m, dir) = await job.RunAsync(new Progress<DownloadProgress>(reports.Add), Ct);
        Assert.Empty(VmQontrol.Core.Machines.MachineChecks.Problems(m, dir));
        Assert.Contains(reports, r => r.Status == "Checking Debian's signature…");
    }
}
