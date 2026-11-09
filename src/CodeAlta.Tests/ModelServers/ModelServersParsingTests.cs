using System.Text.Json;
using CodeAlta.ModelServers;
using CodeAlta.ModelServers.Hardware;
using CodeAlta.ModelServers.HuggingFace;
using CodeAlta.ModelServers.LlamaCpp;
using CodeAlta.ModelServers.Servers;
using CodeAlta.ModelServers.Store;

namespace CodeAlta.Tests.ModelServers;

[TestClass]
public sealed class ModelServersParsingTests
{
    [TestMethod]
    public void ParseAsset_ReadsBackendVersionAndRuntime()
    {
        var cuda = LlamaReleases.ParseAsset("llama-b12110-bin-win-cuda-13.4-x64.zip", "u", 1, "aa")!;
        Assert.AreEqual(("windows", "cuda", "13.4", "x64", false), (cuda.Os, cuda.Backend, cuda.BackendVersion, cuda.Arch, cuda.IsCudaRuntime));
        var runtime = LlamaReleases.ParseAsset("cudart-llama-bin-win-cuda-13.4-x64.zip", "u", 1, "aa")!;
        Assert.IsTrue(runtime.IsCudaRuntime);
        Assert.AreEqual("13.4", runtime.BackendVersion);
        var linuxRuntime = LlamaReleases.ParseAsset("cudart-llama-b12110-bin-ubuntu-cuda-12.8-x64.tar.gz", "u", 1, null)!;
        Assert.AreEqual(("linux", "12.8"), (linuxRuntime.Os, linuxRuntime.BackendVersion));
        Assert.AreEqual("rocm", LlamaReleases.ParseAsset("llama-b12110-bin-ubuntu-rocm-10.0-x64.tar.gz", "u", 1, null)!.Backend);
        Assert.AreEqual("cpu", LlamaReleases.ParseAsset("llama-b12110-bin-ubuntu-x64.tar.gz", "u", 1, null)!.Backend);
        Assert.AreEqual("metal", LlamaReleases.ParseAsset("llama-b12110-bin-macos-arm64.tar.gz", "u", 1, null)!.Backend);
        Assert.AreEqual("cpu", LlamaReleases.ParseAsset("llama-b12110-bin-macos-x64.tar.gz", "u", 1, null)!.Backend);
        Assert.AreEqual("vulkan", LlamaReleases.ParseAsset("llama-b12110-bin-win-vulkan-x64.zip", "u", 1, null)!.Backend);
        Assert.AreEqual("opencl-adreno", LlamaReleases.ParseAsset("llama-b12110-bin-win-opencl-adreno-arm64.zip", "u", 1, null)!.Backend);
        Assert.IsNull(LlamaReleases.ParseAsset("llama-b12110-bin-ubuntu-26.04-s390x.tar.gz", "u", 1, null));
        Assert.IsNull(LlamaReleases.ParseAsset("llama-b12110-bin-android-arm64.tar.gz", "u", 1, null));
        Assert.IsNull(LlamaReleases.ParseAsset("llama-b12110-ui.tar.gz", "u", 1, null));
    }

    [TestMethod]
    public void ReadRelease_SkipsABuildWhoseArchivesAreStillUploading()
    {
        var complete = JsonDocument.Parse("""{"tag_name":"b12110","published_at":"2026-11-04T10:00:00Z","assets":[{"name":"llama-b12110-bin-win-vulkan-x64.zip","browser_download_url":"https://x/v.zip","size":10,"state":"uploaded","digest":"sha256:abc"}]}""");
        var release = LlamaReleases.ReadRelease(complete.RootElement)!;
        Assert.AreEqual(12110, release.Build);
        Assert.AreEqual("abc", release.Assets[0].Sha256);
        var uploading = JsonDocument.Parse("""{"tag_name":"b12111","assets":[{"name":"llama-b12111-bin-win-vulkan-x64.zip","browser_download_url":"u","size":10,"state":"starting"}]}""");
        Assert.IsNull(LlamaReleases.ReadRelease(uploading.RootElement));
        var pointer = JsonDocument.Parse("""{"tag_name":"v0.9.0","assets":[{"name":"nightly-tag.txt","browser_download_url":"u","size":7,"state":"uploaded"}]}""");
        Assert.IsNull(LlamaReleases.ReadRelease(pointer.RootElement));
    }

    [TestMethod]
    public void ParseDevices_ReadsListDevices()
    {
        var devices = LlamaBuilds.ParseDevices("Available devices:\n  Vulkan0: NVIDIA GeForce RTX 4070 (12010 MiB, 11242 MiB free)\r\n  Vulkan2: AMD Radeon RX 7600 XT (16368 MiB, 15554 MiB free)\n");
        Assert.HasCount(2, devices);
        Assert.AreEqual(("Vulkan2", "AMD Radeon RX 7600 XT", 16368L, 15554L), (devices[1].Name, devices[1].Description, devices[1].TotalMiB, devices[1].FreeMiB));
        Assert.IsEmpty(LlamaBuilds.ParseDevices("Available devices:\n  (none)\n"));
        Assert.AreEqual("rocm", LlamaBuilds.DeviceBackend("ROCm1"));
        Assert.AreEqual("cuda", LlamaBuilds.DeviceBackend("CUDA0"));
        Assert.AreEqual("metal", LlamaBuilds.DeviceBackend("MTL0"));
    }

    [TestMethod]
    public void GgufNames_FindsTagsShardsAndProjector()
    {
        HubFile[] files =
        [
            new("Qwen3.8-27B-UD-Q4_K_XL.gguf", 17_548_181_504, "a"),
            new("Qwen3.8-27B-Q8_0.gguf", 29_000_000_000, "b"),
            new("mmproj-BF16.gguf", 931_000_000, "c"),
            new("mmproj-F16.gguf", 931_000_000, "d"),
            new("BF16/Qwen3.8-27B-BF16-00001-of-00002.gguf", 30_000_000_000, "e"),
            new("BF16/Qwen3.8-27B-BF16-00002-of-00002.gguf", 25_000_000_000, "f"),
            new("Q2_K/Qwen3.8-27B-Q2_K-00001-of-00002.gguf", 5_000_000_000, "g"),
            new("imatrix_unsloth.gguf", 10, "h"),
        ];
        var quants = GgufNames.Quantizations(files);
        CollectionAssert.AreEqual(new[] { "UD-Q4_K_XL", "Q8_0", "BF16" }, quants.Select(static q => q.Tag).ToArray(), "a split file with a shard missing is no quantization");
        var ud = GgufNames.Find(files, "UD-Q4_K_XL")!;
        Assert.AreEqual(17_548_181_504, ud.ModelBytes);
        Assert.IsTrue(ud.Vision);
        Assert.AreEqual(4, ud.Bits);
        var bf16 = GgufNames.Find(files, "bf16")!;
        Assert.HasCount(3, bf16.Files, "two shards and the projector");
        Assert.AreEqual(55_000_000_000, bf16.ModelBytes);
        Assert.AreEqual("Q8_0", GgufNames.Find(files, null)!.Tag, "llama.cpp takes Q4_K_M, then Q8_0");
        Assert.IsTrue(GgufNames.IsSidecar("mtp-Qwen3.6-27B-Q8_0.gguf"));
    }

    [TestMethod]
    public void BuildSelector_ChoosesPerFamilyAndSaysWhy()
    {
        var release = new LlamaRelease("b12110", 12110, DateTimeOffset.UtcNow, "v0.9.0",
        [
            Asset("llama-b12110-bin-win-cuda-12.4-x64.zip"), Asset("llama-b12110-bin-win-cuda-13.4-x64.zip"), Asset("cudart-llama-bin-win-cuda-13.4-x64.zip"),
            Asset("llama-b12110-bin-win-rocm-10.0-x64.zip"), Asset("llama-b12110-bin-win-vulkan-x64.zip"), Asset("llama-b12110-bin-win-cpu-x64.zip"),
        ]);
        var windows = new HardwareInventory("windows", "x64", "Ryzen 9 9950X", 64L << 30,
        [
            new GpuInfo("gpu0", "amd", "AMD Radeon RX 7600 XT", 16L << 30, false, "gfx1102", "1002:7480"),
            new GpuInfo("gpu1", "nvidia", "NVIDIA GeForce RTX 4070", 12L << 30, false, "sm_89", "10de:2786"),
            new GpuInfo("gpu2", "amd", "AMD Radeon(TM) Graphics", 512L << 20, true, null, "1002:13c0"),
        ], new MachineLibraries("13.0", "7.1", @"C:\Program Files\AMD\ROCm\7.1", true, true, null), []);
        var candidates = BuildSelector.Candidates(windows, release);
        var cuda = candidates.Single(static c => c.Backend == "cuda");
        Assert.AreEqual(("fits", "llama-b12110-bin-win-cuda-13.4-x64.zip"), (cuda.Verdict, cuda.Asset));
        Assert.AreEqual("fits", candidates.Single(static c => c.Backend == "rocm").Verdict);
        CollectionAssert.AreEqual(new[] { "cuda", "rocm", "vulkan" }, BuildSelector.Recommended(windows, candidates).ToArray());
        var assets = BuildSelector.AssetsFor(release, "windows", "x64", "cuda", cuda);
        CollectionAssert.AreEqual(new[] { "llama-b12110-bin-win-cuda-13.4-x64.zip", "cudart-llama-bin-win-cuda-13.4-x64.zip" }, assets.Select(static a => a.Name).ToArray());

        var oldDriver = windows with { Libraries = windows.Libraries with { CudaDriverVersion = "12.2" } };
        Assert.AreEqual("llama-b12110-bin-win-cuda-12.4-x64.zip", BuildSelector.Candidates(oldDriver, release).Single(static c => c.Backend == "cuda").Asset);
        var noHipSdk = windows with { Libraries = windows.Libraries with { HipBlas = false } };
        var rocm = BuildSelector.Candidates(noHipSdk, release).Single(static c => c.Backend == "rocm");
        Assert.AreEqual("libraryMissing", rocm.Verdict);
        StringAssert.Contains(rocm.Message, "HIP SDK");
        // HIP SDK 6.2: its hipblas.dll loads beside the HIP 7 runtime of the archive and fails at the first product.
        var hipSdk6 = windows with { Libraries = windows.Libraries with { RocmVersion = "6.2" } };
        var old = BuildSelector.Candidates(hipSdk6, release).Single(static c => c.Backend == "rocm");
        Assert.AreEqual("libraryMissing", old.Verdict);
        StringAssert.Contains(old.Message, "HIP SDK 6.2");
        CollectionAssert.AreEqual(new[] { "cuda", "vulkan" }, BuildSelector.Recommended(hipSdk6, BuildSelector.Candidates(hipSdk6, release)).ToArray(), "Vulkan for the AMD GPU instead");
    }

    [TestMethod]
    public void BuildSelector_OnLinuxNeedsRocm7AndGlibc238()
    {
        var release = new LlamaRelease("b12110", 12110, DateTimeOffset.UtcNow, null,
            [Asset("llama-b12110-bin-ubuntu-rocm-10.0-x64.tar.gz"), Asset("llama-b12110-bin-ubuntu-vulkan-x64.tar.gz"), Asset("llama-b12110-bin-ubuntu-x64.tar.gz")]);
        var pcAi = new HardwareInventory("linux", "x64", "AMD Ryzen 9 7950X", 64L << 30,
        [
            new GpuInfo("gpu0", "amd", "AMD Radeon AI PRO R9700", 32624L << 20, false, "gfx1201", "1002:7551"),
            new GpuInfo("gpu1", "amd", "AMD Radeon AI PRO R9700", 32624L << 20, false, "gfx1201", "1002:7551"),
        ], new MachineLibraries(null, "7.2.1", "/opt/rocm-7.2.1", true, true, "2.39"), []);
        var rocm = BuildSelector.Candidates(pcAi, release).Single(static c => c.Backend == "rocm");
        Assert.AreEqual("fits", rocm.Verdict);
        StringAssert.Contains(rocm.Message, "2× AMD Radeon AI PRO R9700");
        var jammy = pcAi with { Libraries = pcAi.Libraries with { Glibc = "2.35" } };
        Assert.AreEqual("systemTooOld", BuildSelector.Candidates(jammy, release).Single(static c => c.Backend == "rocm").Verdict);
        var rocm6 = pcAi with { Libraries = pcAi.Libraries with { RocmVersion = "6.4.0" } };
        Assert.AreEqual("libraryMissing", BuildSelector.Candidates(rocm6, release).Single(static c => c.Backend == "rocm").Verdict);
    }

    [TestMethod]
    public void TheEndOfALog_LeavesOutTheRequestsTheRouterPassesOn()
    {
        using var temp = TestTempDirectory.Create();
        var path = Path.Combine(temp.Path, "server.log");
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < 2000; i++) text.Append("early line ").Append(i).Append('\n');
        text.Append("E ggml_cuda_mul_mat: hipblasGemmEx failed — current device: 1\n");
        text.Append("I srv run: instance name=a/b:Q4_K_M exited with status -1073740791\r\n");
        // The reads of the status, every few seconds, long after the failure: well over one chunk of 64 KB.
        for (var i = 0; i < 4000; i++) text.Append("1.40.433.446 I srv  proxy_reques: proxying request to model a/b:Q4_K_M on port 52980\n");
        File.WriteAllText(path, text.ToString());
        var lines = ServerSupervisor.ReadLogTail(path, 3);
        CollectionAssert.AreEqual(new[] { "early line 1999", "E ggml_cuda_mul_mat: hipblasGemmEx failed — current device: 1", "I srv run: instance name=a/b:Q4_K_M exited with status -1073740791" },
            lines.ToArray());
        Assert.HasCount(10, ServerSupervisor.ReadLogTail(path, 10));
        Assert.AreEqual("early line 0", ServerSupervisor.ReadLogTail(path, 5000)[0], "a short log is read whole");
        Assert.IsEmpty(ServerSupervisor.ReadLogTail(Path.Combine(temp.Path, "none.log"), 5));
    }

    [TestMethod]
    public void WindowsAdapters_OfGpusRemovedFromTheMachine_AreLeftOut()
    {
        // Windows keeps the driver key of a card taken out (an R9700 here) and of an earlier instance of the GPU of the processor.
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"{4d36e968-e325-11ce-bfc1-08002be10318}\0000", @"{4D36E968-E325-11CE-BFC1-08002BE10318}\0002" };
        Assert.IsTrue(HardwareProbe.IsPresentDriver("0000", present));
        Assert.IsTrue(HardwareProbe.IsPresentDriver("0002", present), "the case of the class does not matter");
        Assert.IsFalse(HardwareProbe.IsPresentDriver("0001", present));
        Assert.IsTrue(HardwareProbe.IsPresentDriver("0001", null), "without the devices present, every adapter counts");
        Assert.IsTrue(HardwareProbe.IsPresentDriver("0001", new HashSet<string>()), "nor when none was found on PCI");
    }

    [TestMethod]
    public void LemonadeRocm_TakesThePlaceOfAnOfficialRocmBuildThatCannotRun()
    {
        using var json = JsonDocument.Parse("""
            { "tag_name": "b1371", "published_at": "2026-11-08T14:23:35Z", "assets": [
              { "name": "checksums.txt", "size": 2126, "state": "uploaded", "browser_download_url": "https://x/checksums.txt", "digest": "sha256:aa" },
              { "name": "llama-b1371-windows-rocm-gfx110X-x64.zip", "size": 530775730, "state": "uploaded", "browser_download_url": "https://x/w110", "digest": "sha256:bb" },
              { "name": "llama-b1371-windows-rocm-gfx120X-x64.zip", "size": 688393394, "state": "uploaded", "browser_download_url": "https://x/w120", "digest": "sha256:cc" },
              { "name": "llama-b1371-ubuntu-rocm-gfx120X-x64.zip", "size": 1171721570, "state": "uploaded", "browser_download_url": "https://x/u120", "digest": "sha256:dd" },
              { "name": "qualification-llama-b1371-ubuntu-rocm-gfx120X-x64.json", "size": 155572, "state": "uploaded", "browser_download_url": "https://x/q", "digest": "sha256:ee" }
            ] }
            """);
        var lemonade = LlamaReleases.ReadLemonadeRelease(json.RootElement)!;
        Assert.AreEqual(("lemonade-b1371", "lemonade", 3), (lemonade.Tag, lemonade.Source, lemonade.Assets.Count));
        var w120 = lemonade.Assets.Single(static asset => asset.Name == "llama-b1371-windows-rocm-gfx120X-x64.zip");
        Assert.AreEqual(("windows", "rocm", "gfx120X", "x64", "cc"), (w120.Os, w120.Backend, w120.BackendVersion, w120.Arch, w120.Sha256));
        Assert.IsTrue(LlamaReleases.LemonadeCovers("gfx120X", "gfx1201"));
        Assert.IsTrue(LlamaReleases.LemonadeCovers("gfx1151", "gfx1151"));
        Assert.IsFalse(LlamaReleases.LemonadeCovers("gfx120X", "gfx12010"));
        Assert.IsFalse(LlamaReleases.LemonadeCovers("gfx1151", "gfx1150"));

        var release = new LlamaRelease("b12110", 12110, DateTimeOffset.UtcNow, null,
            [Asset("llama-b12110-bin-win-rocm-10.0-x64.zip"), Asset("llama-b12110-bin-win-vulkan-x64.zip"), Asset("llama-b12110-bin-win-cpu-x64.zip")]);
        var noHipSdk = new HardwareInventory("windows", "x64", null, 64L << 30, [new GpuInfo("gpu0", "amd", "AMD Radeon RX 7600 XT", 16L << 30, false, "gfx1102", null)],
            new MachineLibraries(null, null, null, false, true, null), []);
        Assert.AreEqual("libraryMissing", BuildSelector.Candidates(noHipSdk, release).Single(static c => c.Backend == "rocm").Verdict, "not allowed: the official build's verdict");
        var rocm = BuildSelector.Candidates(noHipSdk, release, lemonade).Single(static c => c.Backend == "rocm");
        Assert.AreEqual(("fits", "llama-b1371-windows-rocm-gfx110X-x64.zip"), (rocm.Verdict, rocm.Asset));
        StringAssert.Contains(rocm.Message, "Lemonade");
        CollectionAssert.AreEqual(new[] { "llama-b1371-windows-rocm-gfx110X-x64.zip" }, BuildSelector.AssetsFor(lemonade, "windows", "x64", "rocm", rocm).Select(static a => a.Name).ToArray());

        var withSdk = noHipSdk with { Libraries = noHipSdk.Libraries with { HipBlas = true, RocmVersion = "7.1" } };
        Assert.AreEqual("llama-b1371-windows-rocm-gfx110X-x64.zip", BuildSelector.Candidates(withSdk with { Libraries = withSdk.Libraries with { RocmVersion = "6.2" } }, release, lemonade)
            .Single(static c => c.Backend == "rocm").Asset, "beside the HIP SDK 6.2, whose hipBLAS the official build cannot use");
        Assert.AreEqual("llama-b12110-bin-win-rocm-10.0-x64.zip", BuildSelector.Candidates(withSdk, release, lemonade).Single(static c => c.Backend == "rocm").Asset,
            "an official build that runs stays the choice");
        var jammy = new HardwareInventory("linux", "x64", null, 64L << 30, [new GpuInfo("gpu0", "amd", "AMD Radeon AI PRO R9700", 32L << 30, false, "gfx1201", null)],
            new MachineLibraries(null, "7.2.1", "/opt/rocm", true, true, "2.35"), []);
        Assert.AreEqual("llama-b1371-ubuntu-rocm-gfx120X-x64.zip", BuildSelector.Candidates(jammy, release, lemonade).Single(static c => c.Backend == "rocm").Asset);
        var unknownGpu = noHipSdk with { Gpus = [noHipSdk.Gpus[0] with { Architecture = "gfx1036" }] };
        Assert.AreEqual("libraryMissing", BuildSelector.Candidates(unknownGpu, release, lemonade).Single(static c => c.Backend == "rocm").Verdict, "Lemonade has no code for it");
    }

    [TestMethod]
    public void DiscreteDevices_LeaveOutTheGpuOfTheProcessor()
    {
        static InstalledBuild Build(params LlamaDevice[] devices) => new("b1", 1, "rocm", null, "f", 1, DateTimeOffset.UnixEpoch, devices, null, true, false, false, "ggml-org");
        var linux = new HardwareInventory("linux", "x64", "AMD Ryzen 9 7950X 16-Core Processor", null,
            [new GpuInfo("0", "amd", "AMD Radeon AI PRO R9700", 32L << 30, false, "gfx1201", null), new GpuInfo("1", "amd", "AMD Radeon Graphics", 512L << 20, true, "gfx1036", null)],
            new MachineLibraries(null, "7.2.1", null, true, true, "2.39"), []);
        var rocm = Build(new LlamaDevice("ROCm0", "AMD Radeon AI PRO R9700", 32624, 32000, null), new LlamaDevice("ROCm1", "AMD Radeon AI PRO R9700", 32624, 32000, null),
            new LlamaDevice("ROCm2", "AMD Ryzen 9 7950X 16-Core Processor", 512, 400, null));
        CollectionAssert.AreEqual(new[] { "ROCm0", "ROCm1" }, LlamaBuilds.DiscreteDevices(rocm, linux).ToArray(), "ROCm names the GPU of the processor after it");
        var vulkan = Build(new LlamaDevice("Vulkan0", "AMD Radeon(TM) Graphics", 16000, 15000, null), new LlamaDevice("Vulkan1", "AMD Radeon AI PRO R9700", 32624, 32000, null));
        CollectionAssert.AreEqual(new[] { "Vulkan1" }, LlamaBuilds.DiscreteDevices(vulkan, linux with { Gpus = [.. linux.Gpus.Select(static gpu => gpu.Integrated ? gpu with { Name = "AMD Radeon(TM) Graphics" } : gpu)] }).ToArray(),
            "Vulkan names it as the inventory does");
        Assert.IsEmpty(LlamaBuilds.DiscreteDevices(Build(new LlamaDevice("CUDA0", "NVIDIA GeForce RTX 4070", 12281, 11000, null)), linux), "a build of discrete GPUs only is left to llama.cpp");
        Assert.IsEmpty(LlamaBuilds.DiscreteDevices(Build(new LlamaDevice("MTL0", "Apple M4 Pro", 40000, 39000, null)),
            linux with { Gpus = [new GpuInfo("0", "apple", "Apple M4 Pro", null, true, null, null)] }), "a machine of one integrated GPU uses it");
    }

    [TestMethod]
    public void IsRefused_CoversTheFamiliesOfOptionsThatReachFilesProgramsOrTheNetwork()
    {
        foreach (var option in new[] { "--video-ffmpeg-dir", "--webui-mcp-proxy", "--grammar-file", "--log-prompts-dir", "--tools", "--model-draft", "-md", "--lora-scaled", "--rpc",
                     "--chat-template-file", "--slot-save-path", "--hf-repo", "-hfr", "--api-key", "--host=0.0.0.0", "--log-file", "--mmproj-url", "--models-max", "--prompt-cache" })
            Assert.IsTrue(ServerArguments.IsRefused(option), option);
        foreach (var option in new[] { "--jinja", "--ctx-size", "-c", "--n-gpu-layers", "-ngl", "--flash-attn", "--cache-type-k", "--cache-reuse", "--temp", "--top-k", "--parallel", "--swa-full",
                     "--reasoning-budget", "--chat-template", "--batch-size", "--split-mode", "--tensor-split", "--no-mmap", "--mlock", "--threads", "--draft-max" })
            Assert.IsFalse(ServerArguments.IsRefused(option), option);
    }

    [TestMethod]
    public void FitEstimate_UsesTheSharedMemoryOfALargeIntegratedGpu()
    {
        var quant = new QuantFiles("Q4_K_M", [], 20_000_000_000, 0, 4);
        HardwareInventory Machine(GpuInfo gpu) => new("linux", "x64", null, 128L << 30, [gpu], new MachineLibraries(null, null, null, false, true, null), []);
        var strixHalo = FitEstimate.For(quant, Machine(new GpuInfo("0", "amd", "AMD Radeon 8060S", 96L << 30, true, "gfx1151", null)));
        Assert.AreEqual("gpu", strixHalo.Fit, "the GPU of a Strix Halo shares 96 GB");
        var mac = FitEstimate.For(quant, Machine(new GpuInfo("0", "apple", "Apple M4 Pro", 24L << 30, true, null, null)));
        Assert.AreEqual("partly", mac.Fit, "a Mac gives its GPU three quarters of its memory");
        var small = FitEstimate.For(quant, Machine(new GpuInfo("0", "amd", "AMD Radeon Graphics", 512L << 20, true, "gfx1036", null)));
        Assert.AreEqual("partly", small.Fit, "a small integrated GPU leaves the model to the processor");
    }

    [TestMethod]
    public void DisplayName_ReadsAsPeopleDo()
    {
        Assert.AreEqual("Qwen3.8 27B · UD-Q4_K_XL", GgufNames.DisplayName("unsloth/Qwen3.8-27B-GGUF:UD-Q4_K_XL"));
        Assert.AreEqual("SmolLM2 135M Instruct · Q4_K_M", GgufNames.DisplayName("bartowski/SmolLM2-135M-Instruct-GGUF:Q4_K_M"));
        Assert.AreEqual("model", GgufNames.DisplayName("org/model"));
    }

    [TestMethod]
    public void HubSearch_KeepsWhatLlamaServerServes()
    {
        HubModel Of(string? pipeline) => new("a/b", "a", 1, 1, null, pipeline, [], false, null);
        Assert.IsTrue(HuggingFaceClient.ServedByLlamaServer(Of("text-generation")));
        Assert.IsTrue(HuggingFaceClient.ServedByLlamaServer(Of("image-text-to-text")));
        Assert.IsTrue(HuggingFaceClient.ServedByLlamaServer(Of("feature-extraction")));
        Assert.IsTrue(HuggingFaceClient.ServedByLlamaServer(Of(null)), "a model that says nothing may be one");
        Assert.IsFalse(HuggingFaceClient.ServedByLlamaServer(Of("text-to-image")));
        Assert.IsFalse(HuggingFaceClient.ServedByLlamaServer(Of("image-to-video")));
    }

    [TestMethod]
    public void CudaVersion_IsReadFromTheHeaderOfOldAndNewDrivers()
    {
        Assert.AreEqual("13.0", HardwareProbe.CudaVersionOf("| NVIDIA-SMI 580.88      Driver Version: 580.88      CUDA Version: 13.0     |"));
        Assert.AreEqual("13.3", HardwareProbe.CudaVersionOf("| NVIDIA-SMI 610.88                 KMD Version: 610.88        CUDA UMD Version: 13.3     |"));
        Assert.IsNull(HardwareProbe.CudaVersionOf("NVIDIA-SMI has failed"));
    }

    [TestMethod]
    public void Gfx_ComesFromTheKfdTargetVersion()
    {
        Assert.AreEqual("gfx1201", HardwareProbe.GfxName(120001));
        Assert.AreEqual("gfx90a", HardwareProbe.GfxName(90010));
        Assert.AreEqual("gfx1151", HardwareProbe.GfxName(110501));
        Assert.AreEqual("gfx1036", HardwareProbe.GfxName(100306));
    }

    [TestMethod]
    public async Task HardwareProbe_ReadsALinuxTree()
    {
        using var temp = TestTempDirectory.Create();
        void Write(string path, string text)
        {
            var full = Path.Combine(temp.Path, path.TrimStart('/'));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        Write("/sys/class/drm/card0/device/vendor", "0x1002\n");
        Write("/sys/class/drm/card0/device/device", "0x7551\n");
        Write("/sys/class/drm/card0/device/mem_info_vram_total", "34208743424\n");
        Write("/sys/class/drm/card1/device/vendor", "0x1002\n");
        Write("/sys/class/drm/card1/device/device", "0x164e\n");
        Write("/sys/class/drm/card1/device/mem_info_vram_total", "536870912\n");
        Write("/sys/class/kfd/kfd/topology/nodes/1/properties", "simd_count 128\ngfx_target_version 120001\nvendor_id 4098\ndevice_id 30033\n");
        Write("/sys/class/kfd/kfd/topology/nodes/2/properties", "simd_count 4\ngfx_target_version 100306\nvendor_id 4098\ndevice_id 5710\n");
        Write("/opt/rocm-7.2.1/.info/version", "7.2.1-81\n");
        Write("/opt/rocm-7.2.1/lib/libamdhip64.so.7", "");
        Write("/opt/rocm-7.2.1/lib/libhipblas.so.3", "");
        Write("/usr/lib/x86_64-linux-gnu/libvulkan.so.1", "");
        Write("/proc/cpuinfo", "processor : 0\nmodel name : AMD Ryzen 9 7950X 16-Core Processor\n");
        Write("/proc/meminfo", "MemTotal:       65000000 kB\n");
        var rocminfo = "  Name:                    gfx1201\n  Marketing Name:          AMD Radeon AI PRO R9700\n  Name:                    gfx1036\n  Marketing Name:          AMD Radeon Graphics\n";
        var probe = new HardwareProbe((file, _, _) => Task.FromResult<string?>(file == "rocminfo" ? rocminfo : null), temp.Path);
        var inventory = await probe.ProbeLinuxAsync("x64", "2.39", CancellationToken.None);
        Assert.HasCount(2, inventory.Gpus);
        Assert.AreEqual(("AMD Radeon AI PRO R9700", "gfx1201", false), (inventory.Gpus[0].Name, inventory.Gpus[0].Architecture, inventory.Gpus[0].Integrated));
        Assert.IsTrue(inventory.Gpus[1].Integrated, "the GPU of the processor goes last");
        Assert.AreEqual(("7.2.1", "/opt/rocm-7.2.1", true, true), (inventory.Libraries.RocmVersion, inventory.Libraries.RocmPath, inventory.Libraries.HipBlas, inventory.Libraries.Vulkan));
        Assert.AreEqual("AMD Ryzen 9 7950X 16-Core Processor", inventory.CpuName);
    }

    [TestMethod]
    public void Import_ReadsARunScriptOfPcAi()
    {
        var script = """
            #!/bin/sh
            export AI_MODEL="unsloth/Qwen3.8-27B-GGUF"
            export HIP_VISIBLE_DEVICES=0
            $LLAMA_HOME/llama-server -hf $AI_MODEL:UD-Q4_K_XL --temp 1.0 --top-p 0.95 --top-k 20 --presence_penalty 0.0 --min-p 0.0 --host pc-ai.home.arpa --reasoning-effort medium -lv 4 --tools all
            """;
        var imported = ServerArguments.Import(script);
        Assert.AreEqual("unsloth/Qwen3.8-27B-GGUF:UD-Q4_K_XL", imported.Model);
        Assert.AreEqual(1.0, imported.Settings!.Sampling.Temperature);
        Assert.AreEqual(20, imported.Settings.Sampling.TopK);
        Assert.AreEqual(0.0, imported.Settings.Sampling.PresencePenalty);
        Assert.AreEqual("medium", imported.Settings.ReasoningEffort);
        CollectionAssert.AreEqual(new[] { 0 }, imported.VisibleDevices.ToArray());
        Assert.IsTrue(imported.Ignored.Any(static item => item.StartsWith("--host", StringComparison.Ordinal)));
        Assert.IsTrue(imported.Ignored.Any(static item => item.StartsWith("--tools", StringComparison.Ordinal)), "an option that runs commands is never taken");
        Assert.IsFalse(imported.Settings.Options.ContainsKey("tools"));
    }

    [TestMethod]
    public void Validate_RefusesOptionsThatRunCommandsOrTouchFiles()
    {
        var server = new ServerDefinition { Id = "coding", Name = "Coding", Backend = "cuda", Port = 9931, Models = [new ServerModel { Model = "a/b:Q4_K_M" }] };
        ServerArguments.Validate(server);
        foreach (var argument in new[] { "--tools", "--mcp-servers-json", "--agent", "--host", "--media-path", "--hf-repo", "--connect", "--slot-save-path", "--api-key" })
        {
            var refused = Assert.ThrowsExactly<ModelHostException>(() => ServerArguments.Validate(server with { ExtraArguments = [argument, "x"] }));
            Assert.AreEqual("refused_option", refused.Code, argument);
        }

        var option = Assert.ThrowsExactly<ModelHostException>(() => ServerArguments.Validate(server with
        {
            Models = [new ServerModel { Model = "a/b:Q4_K_M", Options = new Dictionary<string, string> { ["tools-runtime"] = "ssh:x" } }],
        }));
        Assert.AreEqual("refused_option", option.Code);
        ServerArguments.Validate(server with { ExtraArguments = ["--n-cpu-moe", "8"] });
    }

    [TestMethod]
    public void Preset_NamesModelsByPathWithTheirSettings()
    {
        var server = new ServerDefinition
        {
            Id = "coding", Name = "Coding", Backend = "rocm", Port = 9931, LoadedAtOnce = 2,
            Models =
            [
                new ServerModel { Model = "unsloth/Qwen3.8-27B-GGUF:UD-Q4_K_XL", Devices = ["ROCm0"], ReasoningEffort = "medium",
                    Sampling = new SamplingValues { Temperature = 1.0, TopP = 0.95, TopK = 20 }, FreeGpuAfterMinutes = 30 },
                new ServerModel { Model = "missing/model-GGUF:Q4_K_M" },
            ],
        };
        var (preset, missing) = ServerArguments.Preset(server, model => model.StartsWith("unsloth", StringComparison.Ordinal) ? ("/m/q.gguf", "/m/mmproj.gguf") : null);
        StringAssert.Contains(preset, "[unsloth/Qwen3.8-27B-GGUF:UD-Q4_K_XL]\nmodel = /m/q.gguf\nmmproj = /m/mmproj.gguf\ndevice = ROCm0\nreasoning-effort = medium\ntemp = 1\ntop-p = 0.95\ntop-k = 20\nsleep-idle-seconds = 1800\n");
        CollectionAssert.AreEqual(new[] { "missing/model-GGUF:Q4_K_M" }, missing.ToArray());
        var line = ServerArguments.CommandLine(server, 12110, "/s/keys", "/s/preset.ini", "/s/log", _ => null);
        CollectionAssert.IsSubsetOf(new[] { "--offline", "--no-ui", "--models-max", "2", "--models-memory-margin", "--api-key-file", "--cors-origins" }, line.ToArray());
        Assert.AreEqual("127.0.0.1", line[line.ToList().IndexOf("--host") + 1]);
        var old = ServerArguments.CommandLine(server, 10200, "/s/keys", "/s/preset.ini", "/s/log", _ => null);
        Assert.IsFalse(old.Contains("--models-memory-margin"), "an older router does not know the memory margin");
        Assert.IsFalse(old.Contains("--cors-origins"));
    }

    [TestMethod]
    public void ModelStore_ScansPlacesAndRemovesWithSharedBlobs()
    {
        using var temp = TestTempDirectory.Create();
        var store = new ModelStore(temp.Path);
        var source = Path.Combine(temp.Path, "src");
        Directory.CreateDirectory(source);
        void Put(string path, string sha, int size)
        {
            var part = Path.Combine(source, sha);
            File.WriteAllBytes(part, new byte[size]);
            store.Place("org/model-GGUF", "c0ffee", path, part, sha);
        }

        Put("model-Q4_K_M.gguf", "aaaa", 100);
        Put("model-Q4_K_L.gguf", "aaaa", 100); // the same file under two names, as bartowski publishes
        Put("model-Q8_0.gguf", "bbbb", 200);
        Put("mmproj-F16.gguf", "cccc", 50);
        var models = store.Scan();
        Assert.HasCount(3, models);
        var q4 = models.Single(static m => m.Tag == "Q4_K_M");
        Assert.AreEqual(("org/model-GGUF:Q4_K_M", "c0ffee", 150L, true), (q4.Id, q4.Revision, q4.SizeBytes, q4.Vision));
        Assert.AreEqual(0L, q4.FreedBytes, "its blob and its projector are used by other models");
        var q8 = models.Single(static m => m.Tag == "Q8_0");
        Assert.AreEqual(200L, q8.FreedBytes);
        Assert.AreEqual(200L, store.Remove("org/model-GGUF:Q8_0"));
        Assert.HasCount(2, store.Scan());
        store.Remove("org/model-GGUF:Q4_K_M");
        store.Remove("org/model-GGUF:Q4_K_L");
        Assert.IsEmpty(store.Scan());
        Assert.IsFalse(Directory.Exists(store.RepoFolder("org/model-GGUF")), "a repository left empty goes");
    }

    [TestMethod]
    public void ModelStore_KeepsAQuantizationOfAnOlderRevision_AndNeverDeletesALinkedOriginal()
    {
        using var temp = TestTempDirectory.Create();
        var store = new ModelStore(temp.Path);
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "src")).FullName;
        void Put(string revision, string path, string sha, int size)
        {
            var part = Path.Combine(source, sha);
            File.WriteAllBytes(part, new byte[size]);
            store.Place("org/model-GGUF", revision, path, part, sha);
        }

        Put("old1", "model-Q8_0.gguf", "bbbb", 200);
        Put("new2", "model-Q4_K_M.gguf", "aaaa", 100);
        var models = store.Scan();
        Assert.AreEqual("old1", models.Single(static m => m.Tag == "Q8_0").Revision, "a quantization of an older revision is still on the disk");
        Assert.AreEqual("new2", models.Single(static m => m.Tag == "Q4_K_M").Revision);

        // A snapshot that links to an original elsewhere (a model the person had before): removing it keeps the original.
        var original = Path.Combine(Directory.CreateDirectory(Path.Combine(temp.Path, "elsewhere")).FullName, "mine.gguf");
        File.WriteAllBytes(original, new byte[64]);
        var snapshot = Directory.CreateDirectory(Path.Combine(store.RepoFolder("org/linked-GGUF"), "snapshots", "rev")).FullName;
        try
        {
            File.CreateSymbolicLink(Path.Combine(snapshot, "linked-Q4_K_M.gguf"), original);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return; // this account may not make links: the rest is checked where it may
        }

        store.Remove("org/linked-GGUF:Q4_K_M");
        Assert.IsTrue(File.Exists(original), "the target of a link outside the cache stays");
    }

    [TestMethod]
    public void AModelUpdate_IsSeenByItsDigest_EvenAtTheSameSize()
    {
        var mine = new StoredFile("m-Q4_K_M.gguf", 100, "model", false, "aaaa");
        Assert.IsTrue(ModelHost.SameFile(mine, new HubFile("m-Q4_K_M.gguf", 100, "AAAA")));
        Assert.IsFalse(ModelHost.SameFile(mine, new HubFile("m-Q4_K_M.gguf", 100, "bbbb")), "the same size, other bytes");
        Assert.IsTrue(ModelHost.SameFile(mine with { Sha256 = null }, new HubFile("m-Q4_K_M.gguf", 100, "bbbb")), "a copy without a blob has only its size");
    }

    [TestMethod]
    public void ParseId_RefusesPathsAndOddTags()
    {
        Assert.AreEqual(("unsloth/Qwen3.8-27B-GGUF", "UD-Q4_K_XL"), ModelStore.ParseId("unsloth/Qwen3.8-27B-GGUF:ud-q4_k_xl"));
        Assert.ThrowsExactly<ModelHostException>(() => ModelStore.ParseId("../x/y:Q4"));
        Assert.ThrowsExactly<ModelHostException>(() => ModelStore.ParseId("a/b"));
        Assert.ThrowsExactly<ModelHostException>(() => ModelStore.ParseId("a/b:Q4/../x"));
    }

    private static LlamaAsset Asset(string name) => LlamaReleases.ParseAsset(name, "https://x/" + name, 1000, "00")!;
}