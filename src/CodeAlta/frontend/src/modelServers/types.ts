// The records of the model host of a machine (CodeAlta.ModelServers), as the `modelServers` service gives them: the JSON of
// the node API, in camel case, with the fields the host leaves out when they have no value.

export type GpuInfo = Readonly<{ id: string; vendor: string; name: string; memoryBytes?: number; integrated: boolean; architecture?: string; pciId?: string }>;

export type MachineLibraries = Readonly<{ cudaDriverVersion?: string; rocmVersion?: string; rocmPath?: string; hipBlas: boolean; vulkan: boolean; glibc?: string }>;

export type HardwareInventory = Readonly<{ os: string; architecture: string; cpuName?: string; memoryBytes?: number; gpus: readonly GpuInfo[];
  libraries: MachineLibraries; notes: readonly string[] }>;

export type LlamaDevice = Readonly<{ name: string; description: string; totalMiB: number; freeMiB: number; gpuId?: string }>;

export type LlamaAsset = Readonly<{ name: string; url: string; size: number; sha256?: string; os: string; backend: string; backendVersion?: string; arch: string;
  isCudaRuntime: boolean }>;

export type LlamaRelease = Readonly<{ tag: string; build: number; publishedAt: string; version?: string; assets: readonly LlamaAsset[]; source: string }>;

export type BuildVerdict = "fits" | "noGpu" | "driverTooOld" | "libraryMissing" | "systemTooOld" | "unsupportedGpu" | "notInRelease";

export type BuildCandidate = Readonly<{ backend: string; asset?: string; verdict: BuildVerdict; message: string; score: number; gpuIds: readonly string[] }>;

export type InstalledBuild = Readonly<{ tag: string; build: number; backend: string; backendVersion?: string; folder: string; sizeBytes: number; installedAt: string;
  devices: readonly LlamaDevice[]; versionLine?: string; current: boolean; previous: boolean; bad: boolean; source: string }>;

export type StoredFile = Readonly<{ path: string; size: number; kind: string; shared: boolean; sha256?: string }>;

/** What a model takes on a device, as llama-fit-params measured it, in bytes. */
export type DeviceMemory = Readonly<{ device: string; modelBytes: number; contextBytes: number; computeBytes: number }>;

/** How a model fits the GPUs of its machine, measured there after its download. */
export type MeasuredFit = Readonly<{ backend: string; build: string; context?: number; gpuLayers?: number; arguments: string; devices: readonly DeviceMemory[]; at: string;
  problem?: string }>;

export type StoredModel = Readonly<{ id: string; repo: string; tag: string; revision: string; files: readonly StoredFile[]; sizeBytes: number; freedBytes: number;
  vision: boolean; state: "ready" | "incomplete" | "downloading"; modelPath?: string; mmprojPath?: string; modified?: string; measured?: MeasuredFit }>;

export type DownloadState = "queued" | "downloading" | "checking" | "paused" | "done" | "failed" | "cancelled";

export type DownloadInfo = Readonly<{ id: string; model: string; state: DownloadState; done: number; total: number; bytesPerSecond: number; error?: string;
  errorCode?: string; files: number; createdAt: string; endedAt?: string; revision?: string; requestedBy?: string }>;

export type SamplingValues = Readonly<{ temperature?: number; topP?: number; topK?: number; minP?: number; presencePenalty?: number; repeatPenalty?: number }>;

export type ServerModel = Readonly<{ model: string; devices: readonly string[]; context?: number; parallel?: number; reasoningEffort?: string; sampling: SamplingValues;
  freeGpuAfterMinutes?: number; loadOnStart: boolean; vision: boolean; cacheType?: string; flashAttention?: string; options: Readonly<Record<string, string>> }>;

export type ServerKind = "several" | "one";

export type ServerDefinition = Readonly<{ id: string; name: string; kind: ServerKind; purpose: "chat" | "embedding" | "rerank"; port: number; backend: string;
  build?: string; devices: readonly string[]; loadedAtOnce: number; memoryMarginMiB: number; access: "this-machine" | "my-computers"; enabled: boolean;
  keepRunning: boolean; chatPage: boolean; extraArguments: readonly string[]; models: readonly ServerModel[] }>;

export type ModelState = "unloaded" | "loading" | "loaded" | "sleeping" | "failed" | "missing" | "downloading" | "incomplete";

export type ServerModelStatus = Readonly<{ model: string; state: ModelState; progress?: number; context?: number; trainedContext?: number; busySlots: number; totalSlots: number;
  /** The requests that wait for one of its slots on its machine, those of other machines included. */
  waiting?: number;
  tokensPerSecond?: number; vision: boolean }>;

export type ServerState = "stopped" | "waiting" | "starting" | "ready" | "failed" | "updating";

export type ServerStatus = Readonly<{ state: ServerState; processId?: number; address?: string; buildTag?: string; reason?: string; startedAt?: string; restarts: number;
  models: readonly ServerModelStatus[] }>;

export type ServerView = Readonly<{ definition: ServerDefinition; status: ServerStatus }>;

export type UnmanagedServer = Readonly<{ processId: number; commandLine: string; port?: number }>;

export type UpdatePolicy = Readonly<{ channel: "stable" | "every" | "pinned"; pinnedBuild?: string; schedule: "manual" | "daily" | "weekly"; time: string; day: string;
  updateModels: boolean; allowLemonadeRocm: boolean }>;

export type ModelHostSettings = Readonly<{ modelsFolder?: string; downloadsAtOnce: number; updates: UpdatePolicy }>;

export type PlannedUpdate = Readonly<{ id: string; at: string; build?: string; models: boolean; requestedBy: string }>;

export type UpdateRecord = Readonly<{ at: string; backend: string; from?: string; to: string; result: "updated" | "rolledBack" | "failed"; reason?: string }>;

export type HostTask = Readonly<{ id: string; kind: "install" | "update"; title: string; state: "running" | "waiting" | "done" | "failed"; done: number; total: number;
  message?: string; startedAt: string; endedAt?: string; requestedBy?: string }>;

export type ModelHostStatus = Readonly<{ hardware: HardwareInventory; builds: readonly InstalledBuild[]; candidates: readonly BuildCandidate[]; latest?: LlamaRelease;
  settings: ModelHostSettings; planned?: PlannedUpdate; history: readonly UpdateRecord[]; modelsFolder: string; modelsBytes: number; freeBytes: number;
  models: readonly StoredModel[]; downloads: readonly DownloadInfo[]; servers: readonly ServerView[]; unmanaged: readonly UnmanagedServer[];
  warnings: readonly string[]; tasks: readonly HostTask[]; revision: number }>;

/** One thing asked of a model host (`ModelHostAction`). */
export type ModelHostAction = Readonly<{ action: string; backends?: readonly string[]; backend?: string; build?: string; folder?: string; model?: string; id?: string;
  verb?: string; stop?: boolean; models?: boolean; at?: string; settings?: ModelHostSettings; server?: ServerDefinition; text?: string; name?: string; lines?: number }>;

export type ServerTestResult = Readonly<{ passed: boolean; text?: string; tokensPerSecond?: number; message: string }>;

export type ModelHostActionResult = Readonly<{ server?: ServerView; download?: DownloadInfo; task?: HostTask; test?: ServerTestResult; log?: readonly string[];
  imported?: ServerDefinition; notes?: readonly string[]; freed?: number; planned?: PlannedUpdate; modelIds?: readonly string[]; build?: InstalledBuild;
  hardware?: HardwareInventory; latest?: LlamaRelease; measured?: MeasuredFit }>;

export type HubModel = Readonly<{ id: string; author: string; downloads: number; likes: number; lastModified?: string; pipelineTag?: string; tags: readonly string[];
  gated: boolean; trending?: number }>;

export type FitResult = Readonly<{ fit: "gpu" | "gpus" | "partly" | "too_large"; message: string; approximate: boolean; context?: number }>;

/** What a model needs in memory besides its weights, from the header of its GGUF file. */
export type GgufShape = Readonly<{ architecture: string; layers: number; trainedContext: number; kvBytesPerToken: number; slidingKvBytesPerToken: number; slidingWindow: number;
  vocabulary: number; known: boolean }>;

export type HubFitView = Readonly<{ machine: string; fit: FitResult; onMachine: boolean }>;

export type HubQuantView = Readonly<{ tag: string; bits: number; modelBytes: number; mmprojBytes: number; files: number; fits: readonly HubFitView[] }>;

export type HubModelView = Readonly<{ model: HubModel; revision: string; license?: string; baseModels: readonly string[]; parameters?: number; architecture?: string;
  contextLength?: number; quantizations: readonly HubQuantView[]; card: string; shape?: GgufShape }>;

/** A machine whose model servers the window shows (`ModelServersMachine`). */
export type ModelServersMachine = Readonly<{ id: string; name: string; local: boolean; state: string; manages: boolean; uses: boolean; problem?: string | null }>;
