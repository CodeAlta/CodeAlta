using System.Text.Json.Serialization;
using CodeAlta.ModelServers.Downloads;
using CodeAlta.ModelServers.LlamaCpp;
using CodeAlta.ModelServers.Servers;

namespace CodeAlta.ModelServers;

/// <summary>A GPU of a machine, as the system describes it. What is not known stays null: nothing is guessed.</summary>
/// <param name="Id">A stable id within the machine (<c>gpu0</c>...).</param>
/// <param name="Vendor"><c>nvidia</c>, <c>amd</c>, <c>intel</c>, <c>apple</c> or <c>other</c>.</param>
/// <param name="Name">Its name, such as <c>AMD Radeon AI PRO R9700</c>.</param>
/// <param name="MemoryBytes">Its own memory, or null when the system does not say.</param>
/// <param name="Integrated">Whether it is the GPU of the processor, which shares the memory of the machine.</param>
/// <param name="Architecture">Its architecture when known: <c>gfx1201</c> for AMD, <c>sm_89</c> for NVIDIA.</param>
/// <param name="PciId">Its PCI vendor and device ids (<c>1002:7551</c>), when known.</param>
public sealed record GpuInfo(string Id, string Vendor, string Name, long? MemoryBytes, bool Integrated, string? Architecture, string? PciId);

/// <summary>The libraries of a machine that decide which llama.cpp builds can run there.</summary>
/// <param name="CudaDriverVersion">The CUDA version the NVIDIA driver supports (<c>13.0</c>), or null without one.</param>
/// <param name="RocmVersion">The ROCm installed on Linux (<c>7.2.1</c>), or the HIP SDK on Windows (<c>6.2</c>), or null.</param>
/// <param name="RocmPath">Where that ROCm or HIP SDK is, or null.</param>
/// <param name="HipBlas">Whether the HIP BLAS library the ROCm builds need is there.</param>
/// <param name="Vulkan">Whether the Vulkan loader is there.</param>
/// <param name="Glibc">The glibc version on Linux (<c>2.39</c>), or null.</param>
public sealed record MachineLibraries(string? CudaDriverVersion, string? RocmVersion, string? RocmPath, bool HipBlas, bool Vulkan, string? Glibc);

/// <summary>What a machine has for running models.</summary>
/// <param name="Os"><c>windows</c>, <c>linux</c> or <c>macos</c>.</param>
/// <param name="Architecture"><c>x64</c> or <c>arm64</c>.</param>
/// <param name="CpuName">The name of the processor, when known.</param>
/// <param name="MemoryBytes">The memory of the machine, when known.</param>
/// <param name="Gpus">Its GPUs, discrete ones first.</param>
/// <param name="Libraries">The libraries found.</param>
/// <param name="Notes">What could not be read, for people.</param>
public sealed record HardwareInventory(string Os, string Architecture, string? CpuName, long? MemoryBytes, IReadOnlyList<GpuInfo> Gpus,
    MachineLibraries Libraries, IReadOnlyList<string> Notes);

/// <summary>A device a llama.cpp build sees (<c>--list-devices</c>).</summary>
/// <param name="Name">The name a server is given (<c>CUDA0</c>, <c>ROCm1</c>, <c>Vulkan2</c>).</param>
/// <param name="Description">Its description, such as <c>NVIDIA GeForce RTX 4070</c>.</param>
/// <param name="TotalMiB">Its memory in MiB.</param>
/// <param name="FreeMiB">Its free memory in MiB when the build looked.</param>
/// <param name="GpuId">The GPU of the inventory it is, when matched.</param>
public sealed record LlamaDevice(string Name, string Description, long TotalMiB, long FreeMiB, string? GpuId);

/// <summary>An archive of a llama.cpp release.</summary>
/// <param name="Name">Its file name.</param>
/// <param name="Url">Where it is downloaded from.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="Sha256">Its SHA-256 in lowercase hexadecimal, as the release publishes it, or null.</param>
/// <param name="Os"><c>windows</c>, <c>linux</c> or <c>macos</c>.</param>
/// <param name="Backend"><c>cpu</c>, <c>cuda</c>, <c>rocm</c>, <c>vulkan</c>, <c>metal</c>, <c>sycl</c> or another name.</param>
/// <param name="BackendVersion">The version of the backend in the name (<c>13.4</c> for CUDA), or null.</param>
/// <param name="Arch"><c>x64</c> or <c>arm64</c>.</param>
/// <param name="IsCudaRuntime">Whether it is the CUDA runtime archive that goes with a CUDA build.</param>
public sealed record LlamaAsset(string Name, string Url, long Size, string? Sha256, string Os, string Backend, string? BackendVersion, string Arch, bool IsCudaRuntime);

/// <summary>A build of llama.cpp in its releases.</summary>
/// <param name="Tag">Its tag, such as <c>b12110</c>.</param>
/// <param name="Build">Its number, which orders builds.</param>
/// <param name="PublishedAt">When it was published.</param>
/// <param name="Version">The versioned release that names it (<c>v0.9.0</c>), or null.</param>
/// <param name="Assets">Its archives.</param>
/// <param name="Source">Where it comes from: <c>ggml-org</c> or <c>lemonade</c>.</param>
public sealed record LlamaRelease(string Tag, int Build, DateTimeOffset PublishedAt, string? Version, IReadOnlyList<LlamaAsset> Assets, string Source = "ggml-org");

/// <summary>What a build of one backend would do on this machine, and why.</summary>
/// <param name="Backend">The backend.</param>
/// <param name="Asset">The archive it takes, or null when the release has none for this machine.</param>
/// <param name="Verdict"><c>fits</c>, <c>noGpu</c>, <c>driverTooOld</c>, <c>libraryMissing</c>, <c>systemTooOld</c>, <c>unsupportedGpu</c> or <c>notInRelease</c>.</param>
/// <param name="Message">What it means, for people.</param>
/// <param name="Score">How good it is when it fits: the higher the better.</param>
/// <param name="GpuIds">The GPUs it is for.</param>
public sealed record BuildCandidate(string Backend, string? Asset, string Verdict, string Message, int Score, IReadOnlyList<string> GpuIds);

/// <summary>A build installed on the machine.</summary>
/// <param name="Tag">Its tag, or <c>own</c> for a folder of the person's own.</param>
/// <param name="Build">Its number, 0 for one of the person's own.</param>
/// <param name="Backend">Its backend.</param>
/// <param name="BackendVersion">The version of its backend, or null.</param>
/// <param name="Folder">Where it is.</param>
/// <param name="SizeBytes">What it takes on disk.</param>
/// <param name="InstalledAt">When it was installed.</param>
/// <param name="Devices">The devices it sees.</param>
/// <param name="VersionLine">What <c>--version</c> printed.</param>
/// <param name="Current">Whether servers of this backend run it.</param>
/// <param name="Previous">Whether it is the one kept to go back to.</param>
/// <param name="Bad">Whether it failed its test here.</param>
/// <param name="Source"><c>ggml-org</c>, <c>lemonade</c> or <c>own</c>.</param>
public sealed record InstalledBuild(string Tag, int Build, string Backend, string? BackendVersion, string Folder, long SizeBytes, DateTimeOffset InstalledAt,
    IReadOnlyList<LlamaDevice> Devices, string? VersionLine, bool Current, bool Previous, bool Bad, string Source);

/// <summary>A file of a model on disk.</summary>
/// <param name="Path">Its path in the snapshot of its repository.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="Kind"><c>model</c>, <c>mmproj</c> or <c>draft</c>.</param>
/// <param name="Shared">Whether another model of the repository uses it too.</param>
/// <param name="Sha256">Its SHA-256, the name of its blob; null for a copy without a blob (no right to make links).</param>
public sealed record StoredFile(string Path, long Size, string Kind, bool Shared, string? Sha256 = null);

/// <summary>A model on disk: one quantization of a repository at a revision, with its parts.</summary>
/// <param name="Id">Its id, <c>org/repo:TAG</c>.</param>
/// <param name="Repo">Its repository.</param>
/// <param name="Tag">Its quantization.</param>
/// <param name="Revision">The commit it was downloaded at.</param>
/// <param name="Files">Its files.</param>
/// <param name="SizeBytes">What it takes, each file counted once.</param>
/// <param name="FreedBytes">What removing it frees: its files no other model uses.</param>
/// <param name="Vision">Whether it has a vision projector.</param>
/// <param name="State"><c>ready</c>, <c>incomplete</c> or <c>downloading</c>.</param>
/// <param name="ModelPath">The full path of its main file (the first shard of a split one).</param>
/// <param name="MmprojPath">The full path of its vision projector, or null.</param>
/// <param name="Modified">When its files were written.</param>
/// <param name="Measured">How it fits the GPUs of the machine, as <c>llama-fit-params</c> measured it after its download; null before.</param>
public sealed record StoredModel(string Id, string Repo, string Tag, string Revision, IReadOnlyList<StoredFile> Files, long SizeBytes, long FreedBytes, bool Vision,
    string State, string? ModelPath, string? MmprojPath, DateTimeOffset? Modified, MeasuredFit? Measured = null);

/// <summary>What a model takes on a device, as <c>llama-fit-params</c> measured it, in bytes.</summary>
/// <param name="Device">The device (<c>CUDA0</c>, <c>ROCm1</c>, <c>Host</c> for the memory of the system).</param>
/// <param name="ModelBytes">Its weights there.</param>
/// <param name="ContextBytes">Its KV cache there.</param>
/// <param name="ComputeBytes">Its buffers of computation there.</param>
public sealed record DeviceMemory(string Device, long ModelBytes, long ContextBytes, long ComputeBytes);

/// <summary>How a model fits the GPUs of its machine, measured there by the build of llama.cpp that serves it.</summary>
/// <param name="Backend">The backend of the build.</param>
/// <param name="Build">The build.</param>
/// <param name="Context">The context that fits, in tokens; null when it did not say.</param>
/// <param name="GpuLayers">The layers on the GPUs; -1 for all of them.</param>
/// <param name="Arguments">What it chose, as the arguments of llama-server (<c>-c 40960 -ngl -1</c>).</param>
/// <param name="Devices">What each device takes.</param>
/// <param name="At">When.</param>
/// <param name="Problem">Why it could not measure; null when it did.</param>
public sealed record MeasuredFit(string Backend, string Build, int? Context, int? GpuLayers, string Arguments, IReadOnlyList<DeviceMemory> Devices, DateTimeOffset At,
    string? Problem = null);

/// <summary>A download of a model.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Model">The model, <c>org/repo:TAG</c>.</param>
/// <param name="State"><c>queued</c>, <c>downloading</c>, <c>checking</c>, <c>paused</c>, <c>done</c>, <c>failed</c> or <c>cancelled</c>.</param>
/// <param name="Done">Bytes on disk.</param>
/// <param name="Total">Bytes in all.</param>
/// <param name="BytesPerSecond">The speed over the last seconds.</param>
/// <param name="Error">Why it failed, for people.</param>
/// <param name="ErrorCode">Why it failed: <c>disk</c>, <c>network</c>, <c>gated</c>, <c>not_found</c>, <c>checksum</c>, <c>rate_limited</c>.</param>
/// <param name="Files">How many files it fetches.</param>
/// <param name="CreatedAt">When it was asked.</param>
/// <param name="EndedAt">When it ended, or null.</param>
/// <param name="Revision">The commit it downloads, once known.</param>
/// <param name="RequestedBy">Who asked for it (a device, an agent session), or null.</param>
public sealed record DownloadInfo(string Id, string Model, string State, long Done, long Total, double BytesPerSecond, string? Error, string? ErrorCode,
    int Files, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt, string? Revision = null, string? RequestedBy = null);

/// <summary>Sampling values a model starts with; a request may still give its own.</summary>
public sealed record SamplingValues
{
    /// <summary>Gets the temperature.</summary>
    public double? Temperature { get; init; }

    /// <summary>Gets the nucleus sampling threshold.</summary>
    public double? TopP { get; init; }

    /// <summary>Gets how many tokens top-k keeps.</summary>
    public int? TopK { get; init; }

    /// <summary>Gets the minimum probability.</summary>
    public double? MinP { get; init; }

    /// <summary>Gets the presence penalty.</summary>
    public double? PresencePenalty { get; init; }

    /// <summary>Gets the repeat penalty.</summary>
    public double? RepeatPenalty { get; init; }
}

/// <summary>A model a server serves, with its settings.</summary>
public sealed record ServerModel
{
    /// <summary>Gets the model, <c>org/repo:TAG</c>.</summary>
    public required string Model { get; init; }

    /// <summary>Gets the devices it runs on, or empty for those of the server.</summary>
    public IReadOnlyList<string> Devices { get; init; } = [];

    /// <summary>Gets the context in tokens, or null for what <c>--fit</c> chooses.</summary>
    public int? Context { get; init; }

    /// <summary>Gets how many requests it serves at once, or null for llama.cpp's choice.</summary>
    public int? Parallel { get; init; }

    /// <summary>Gets the reasoning effort (<c>low</c>, <c>medium</c>, <c>high</c>), or null.</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>Gets the sampling values.</summary>
    public SamplingValues Sampling { get; init; } = new();

    /// <summary>Gets after how many idle minutes it frees the GPU, or null to keep it.</summary>
    public int? FreeGpuAfterMinutes { get; init; }

    /// <summary>Gets whether it loads when the server starts.</summary>
    public bool LoadOnStart { get; init; }

    /// <summary>Gets whether its vision projector is loaded when it has one.</summary>
    public bool Vision { get; init; } = true;

    /// <summary>Gets the KV cache type (<c>f16</c>, <c>q8_0</c>, <c>q4_0</c>), or null for llama.cpp's.</summary>
    public string? CacheType { get; init; }

    /// <summary>Gets the flash attention setting (<c>on</c>, <c>off</c>, <c>auto</c>), or null.</summary>
    public string? FlashAttention { get; init; }

    /// <summary>Gets other options of the model, by their llama.cpp name without dashes (checked before use).</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();
}

/// <summary>A model server: a llama-server on this machine, as the person set it.</summary>
public sealed record ServerDefinition
{
    /// <summary>Gets its id: letters, digits and <c>-</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Gets its name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets <c>several</c> (router mode) or <c>one</c> (one model always loaded).</summary>
    public string Kind { get; init; } = ServerKinds.Several;

    /// <summary>Gets <c>chat</c>, <c>embedding</c> or <c>rerank</c>; only a one-model server serves embeddings or rerank.</summary>
    public string Purpose { get; init; } = "chat";

    /// <summary>Gets its port on loopback.</summary>
    public int Port { get; init; }

    /// <summary>Gets the backend of the build it runs (<c>cuda</c>, <c>rocm</c>, <c>vulkan</c>...).</summary>
    public required string Backend { get; init; }

    /// <summary>Gets the build it stays on, or null for the current build of its backend.</summary>
    public string? Build { get; init; }

    /// <summary>Gets the devices of the server (<c>ROCm0</c>...), or empty for llama.cpp's choice.</summary>
    public IReadOnlyList<string> Devices { get; init; } = [];

    /// <summary>Gets how many models it keeps loaded at once (router mode).</summary>
    public int LoadedAtOnce { get; init; } = 1;

    /// <summary>Gets the memory, in MiB, it keeps free per device when it chooses what to unload.</summary>
    public int MemoryMarginMiB { get; init; } = 1024;

    /// <summary>Gets who reaches it: <c>this-machine</c> or <c>my-computers</c>.</summary>
    public string Access { get; init; } = "my-computers";

    /// <summary>Gets whether it should run: it starts with its host.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Gets whether it keeps running when CodeAlta Desktop quits.</summary>
    public bool KeepRunning { get; init; }

    /// <summary>Gets whether its chat page is shown.</summary>
    public bool ChatPage { get; init; }

    /// <summary>Gets the arguments added to its command line (checked before use).</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>Gets its models.</summary>
    public IReadOnlyList<ServerModel> Models { get; init; } = [];
}

/// <summary>The kinds of model servers.</summary>
public static class ServerKinds
{
    /// <summary>Router mode: models load on demand.</summary>
    public const string Several = "several";

    /// <summary>One model, always loaded.</summary>
    public const string One = "one";
}

/// <summary>Where a model of a server stands.</summary>
/// <param name="Model">The model.</param>
/// <param name="State"><c>unloaded</c>, <c>loading</c>, <c>loaded</c>, <c>sleeping</c>, <c>failed</c> or <c>missing</c> (not on disk).</param>
/// <param name="Progress">Its loading progress from 0 to 1, or null.</param>
/// <param name="Context">The context it runs with, or had when it last loaded.</param>
/// <param name="TrainedContext">The context it was trained for, or null.</param>
/// <param name="BusySlots">How many of its requests work now.</param>
/// <param name="TotalSlots">How many requests it serves at once.</param>
/// <param name="TokensPerSecond">Its generation speed over the last requests, or null.</param>
/// <param name="Vision">Whether it reads images.</param>
public sealed record ServerModelStatus(string Model, string State, double? Progress, int? Context, int? TrainedContext, int BusySlots, int TotalSlots,
    double? TokensPerSecond, bool Vision);

/// <summary>Where a server stands.</summary>
/// <param name="State"><c>stopped</c>, <c>waiting</c> (for its build or a model), <c>starting</c>, <c>ready</c>, <c>failed</c> or <c>updating</c>.</param>
/// <param name="ProcessId">Its process, or null.</param>
/// <param name="Address">Its address on loopback, or null.</param>
/// <param name="BuildTag">The build it runs, or null.</param>
/// <param name="Reason">Why it failed, or what it waits for, for people.</param>
/// <param name="StartedAt">When it started, or null.</param>
/// <param name="Restarts">How many times it was restarted after a failure in a row.</param>
/// <param name="Models">Its models.</param>
public sealed record ServerStatus(string State, int? ProcessId, string? Address, string? BuildTag, string? Reason, DateTimeOffset? StartedAt, int Restarts,
    IReadOnlyList<ServerModelStatus> Models)
{
    /// <summary>A server that does not run.</summary>
    public static ServerStatus Stopped { get; } = new("stopped", null, null, null, null, null, 0, []);
}

/// <summary>A server and where it stands.</summary>
/// <param name="Definition">What the person set.</param>
/// <param name="Status">Where it stands.</param>
public sealed record ServerView(ServerDefinition Definition, ServerStatus Status);

/// <summary>A llama-server that runs on the machine and that CodeAlta did not start.</summary>
/// <param name="ProcessId">Its process.</param>
/// <param name="CommandLine">Its command line.</param>
/// <param name="Port">Its port, when its command line names one.</param>
public sealed record UnmanagedServer(int ProcessId, string CommandLine, int? Port);

/// <summary>When and how a machine updates llama.cpp, and its models.</summary>
public sealed record UpdatePolicy
{
    /// <summary>Gets <c>stable</c> (the build of llama.cpp's versioned releases), <c>every</c> (every build at least a day old) or <c>pinned</c>.</summary>
    public string Channel { get; init; } = "stable";

    /// <summary>Gets the build a <c>pinned</c> channel stays on.</summary>
    public string? PinnedBuild { get; init; }

    /// <summary>Gets <c>manual</c> (when the person clicks Update), <c>daily</c> or <c>weekly</c>.</summary>
    public string Schedule { get; init; } = "manual";

    /// <summary>Gets the time of day of an automatic update, <c>HH:mm</c> in the machine's time zone.</summary>
    public string Time { get; init; } = "04:00";

    /// <summary>Gets the day of a weekly update (<c>sunday</c>...).</summary>
    public string Day { get; init; } = "sunday";

    /// <summary>Gets whether the machine updates its models by itself on the same schedule.</summary>
    public bool UpdateModels { get; init; }

    /// <summary>Gets whether the ROCm builds of lemonade-sdk/llamacpp-rocm may be used.</summary>
    public bool AllowLemonadeRocm { get; init; }
}

/// <summary>An update planned once.</summary>
/// <param name="Id">Its id.</param>
/// <param name="At">When it runs.</param>
/// <param name="Build">The build it installs, or null for the newest of the channel.</param>
/// <param name="Models">Whether it updates the models too.</param>
/// <param name="RequestedBy">Who asked for it.</param>
public sealed record PlannedUpdate(string Id, DateTimeOffset At, string? Build, bool Models, string RequestedBy);

/// <summary>An update that happened.</summary>
/// <param name="At">When.</param>
/// <param name="Backend">The backend.</param>
/// <param name="From">The build before.</param>
/// <param name="To">The build tried.</param>
/// <param name="Result"><c>updated</c>, <c>rolledBack</c> or <c>failed</c>.</param>
/// <param name="Reason">Why, for people.</param>
public sealed record UpdateRecord(DateTimeOffset At, string Backend, string? From, string To, string Result, string? Reason);

/// <summary>The settings of a machine's model host.</summary>
public sealed record ModelHostSettings
{
    /// <summary>Gets the folder of the models, or null for the machine's Hugging Face cache.</summary>
    public string? ModelsFolder { get; init; }

    /// <summary>Gets how many downloads run at once.</summary>
    public int DownloadsAtOnce { get; init; } = 2;

    /// <summary>Gets the update policy.</summary>
    public UpdatePolicy Updates { get; init; } = new();
}

/// <summary>Everything a machine's model host says, in one record.</summary>
/// <param name="Hardware">Its GPUs and libraries.</param>
/// <param name="Builds">The installed builds.</param>
/// <param name="Candidates">What each backend would do here.</param>
/// <param name="Latest">The build the channel offers now, or null when not read yet.</param>
/// <param name="Settings">The settings.</param>
/// <param name="Planned">An update planned once, or null.</param>
/// <param name="History">The last updates.</param>
/// <param name="ModelsFolder">Where models are.</param>
/// <param name="ModelsBytes">What models take.</param>
/// <param name="FreeBytes">What is free on the disk of the models.</param>
/// <param name="Models">The models on disk.</param>
/// <param name="Downloads">The downloads.</param>
/// <param name="Servers">The servers.</param>
/// <param name="Unmanaged">The llama-servers CodeAlta did not start.</param>
/// <param name="Warnings">What a person should know (a llama.cpp config.ini, a machine shared by several people).</param>
/// <param name="Tasks">The installs and updates running or just ended.</param>
/// <param name="Revision">Grows with each change.</param>
public sealed record ModelHostStatus(HardwareInventory Hardware, IReadOnlyList<InstalledBuild> Builds, IReadOnlyList<BuildCandidate> Candidates, LlamaRelease? Latest,
    ModelHostSettings Settings, PlannedUpdate? Planned, IReadOnlyList<UpdateRecord> History, string ModelsFolder, long ModelsBytes, long FreeBytes,
    IReadOnlyList<StoredModel> Models, IReadOnlyList<DownloadInfo> Downloads, IReadOnlyList<ServerView> Servers, IReadOnlyList<UnmanagedServer> Unmanaged,
    IReadOnlyList<string> Warnings, IReadOnlyList<HostTask> Tasks, long Revision);

/// <summary>An install or an update of llama.cpp that takes time.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Kind"><c>install</c> or <c>update</c>.</param>
/// <param name="Title">What it does, for people.</param>
/// <param name="State"><c>running</c>, <c>waiting</c> (for idle servers), <c>done</c> or <c>failed</c>.</param>
/// <param name="Done">Bytes done of its downloads.</param>
/// <param name="Total">Bytes in all.</param>
/// <param name="Message">Where it stands, or why it failed.</param>
/// <param name="StartedAt">When it started.</param>
/// <param name="EndedAt">When it ended, or null.</param>
/// <param name="RequestedBy">Who asked for it, or null.</param>
public sealed record HostTask(string Id, string Kind, string Title, string State, long Done, long Total, string? Message, DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    string? RequestedBy);

/// <summary>Why an operation of the model host was refused.</summary>
public sealed class ModelHostException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ModelHostException"/> class.</summary>
    /// <param name="code">The reason, such as <c>not_found</c> or <c>refused_option</c>.</param>
    /// <param name="message">What it means, for people.</param>
    public ModelHostException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Gets the reason.</summary>
    public string Code { get; }
}

/// <summary>The JSON of the model host: its files and its records on the wire.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ModelHostStatus))]
[JsonSerializable(typeof(ServerDefinition))]
[JsonSerializable(typeof(ModelHostSettings))]
[JsonSerializable(typeof(PlannedUpdate))]
[JsonSerializable(typeof(List<UpdateRecord>))]
[JsonSerializable(typeof(Dictionary<string, MeasuredFit>))]
[JsonSerializable(typeof(List<LlamaRelease>))]
[JsonSerializable(typeof(BuildState))]
[JsonSerializable(typeof(ServerRun))]
[JsonSerializable(typeof(DownloadJournal))]
[JsonSerializable(typeof(List<DownloadInfo>))]
[JsonSerializable(typeof(HardwareInventory))]
public sealed partial class ModelServersJsonContext : JsonSerializerContext;
