# Project Memory

This document records durable project context so future work can begin from the
existing architecture rather than rediscovering it. Keep it limited to stable
structure, ownership, data flow, and verification paths. Do not use it for
open issues, debugging notes, temporary workarounds, or the outcome of a
specific change.

## Purpose and environment

- This is the Unity example project for the in-repository Svelto.Tasks work in
  progress.
- Unity Editor: `6000.5.9f1`.
- The top-level `README.md` is intentionally brief; the packages contain their
  own API-oriented documentation.

## Repository map

```
Assets/
  Startup/                           Project entry scene and scene-selection menu
    Startup.unity                     Build index 0 project entry scene
    StartupSceneMenu.cs               Dynamic enabled-build-scene selector
  Labyrinth/                         Labyrinth path-search examples and scene
  MillionPoints/                     One-million-particle comparison examples
    MillionPoints.unity                Primary MillionPoints scene
    MillionPoints.mat                 Instanced-point material
    ComputeShadersKernel/             GPU compute implementation
    SveltoCPUKernel/                  Svelto.Tasks CPU implementations
    UnityJobsKernel/                  Unity Jobs CPU reference implementation
    PerformanceProfiler/              Profiling UI and sample assets
Packages/
  com.sebaslab.svelto.common/         Svelto.Common source package
  com.sebaslab.svelto.tasks/          Svelto.Tasks source package and tests
```

`Svelte.Common` and `Svelte.Tasks` are the package assembly names. The package
directory names use the `com.sebaslab...` naming convention.

## Startup scene flow

`Assets/Startup/Startup.unity` is the project entry scene and must remain at
enabled Build Settings index 0. It contains a camera, directional light, and a
`StartupSceneMenu` component.

`StartupSceneMenu` treats the enabled Build Settings scene list as its catalog.
It enumerates that list at runtime, excludes only the active startup scene by
its scene path, derives each button label from the scene file name, and loads a
selected scene by its Build Settings index. It intentionally has no hard-coded
scene names or exclusions, so maintaining the Build Settings list controls the
available menu choices.

The verified Build Settings order is:

1. `Assets/Startup/Startup.unity`
2. `Assets/Labyrinth/LabyrinthDemo.unity`
3. `Assets/MillionPoints/main.unity`

## MillionPoints

MillionPoints compares ways to animate and render a large point set. Every
implementation renders through `Graphics.DrawMeshInstancedIndirect`; the
comparison is about where particle positions are calculated and how those
positions reach the GPU.

### Shared rendering model

1. Initialize particle positions, per-particle rotation speeds, static albedos,
   a one-vertex point mesh, and indirect-instancing arguments.
2. Upload dynamic `float3` positions to `_ParticleDataBuffer` every frame and
   static `float3` albedos to `_AlbedoBuffer` once during initialization.
3. The point shader reads both buffers for `unity_InstanceID`, assigns the
   position and color, and renders the indirect instance.

All CPU renderers use this structure-of-arrays contract: both buffers have a
12-byte `float3` stride. The compute implementation intentionally retains its
own position-plus-albedo record, base position, and rotation speed in its
separate compute-shader contract.

### Svelto.Tasks CPU implementations

Every Svelto CPU example is a standalone `MonoBehaviour`; there is no shared
base class. This is intentional: the examples compare scheduling policies, so
each owns its renderer resources, native storage, lifecycle, and any coordinator
thread it actually needs. The duplicate setup is preferable to inherited hidden
state in this benchmark code.

`MillionPointsCPU_BurstSync.cs` is the direct Svelto.Tasks counterpart to the
Unity Jobs reference. It owns Svelto.Common `NativeDynamicArray` allocations
for input and persistent frame time, plus a GPU-only static albedo buffer.

`MillionPointsPipelinedBurstSupport.cs` is stateless support used only by
AdvancedSync and IndependentThreads. Its `PipelinedBurstRangeTask` and static
Burst kernel use a packed two-slot `float3` output allocation. A native
write-slot value selects the output offset for each pass, avoiding global state
and allowing the range-task prototype to stay immutable after `Add()`.

All Svelto CPU examples use `MultiThreadedBurstParallelTaskCollection<TTask>`
with `Environment.ProcessorCount - 1` workers. The collection performs managed
range scheduling; each concrete task calls a statically-known Burst kernel.

The attachable strategies are:

- `MillionPointsCPU_BurstSync`: opens the mapped upload region, publishes it
  through a Burst `SharedStatic`, completes the Burst collection, closes the
  write, then draws.
- `MillionPointsCPU_AdvancedSync`: owns a private coordinator
  `MultiThreadRunner` solely for the signal handshake. It runs Burst pass N+1
  into the other result slot while the main thread copies slot N into the mapped
  upload region and renders it.
- `MillionPointsCPU_IndependentThreads`: owns a private coordinator
  `MultiThreadRunner` solely for its latest-wins producer loop. It runs Burst
  passes into two slots and publishes generations; the main thread copies and
  renders the newest completed slot without waiting.

### BeginWrite/SubUpdates upload protocol

Every CPU position buffer is created with `ComputeBufferMode.SubUpdates` and
uses one full-range `BeginWrite<float3>`/`EndWrite<float3>` cycle whenever it
uploads a completed position set.
Unity's 6000.5 `ComputeBuffer.BeginWrite` documentation says this path always
uses fewer memory copies than `SetData`; its performance still varies according
to whether Unity can map GPU memory directly or returns temporary CPU memory.

BurstSync and Unity Jobs fill the newly opened region directly from their
respective workers. AdvancedSync and IndependentThreads retain their two-slot
CPU result buffers so their synchronization policies stay comparable; the main
thread copies the completed slot into the newly opened region before `EndWrite`.
BurstSync publishes the fresh region through a
`Unity.Burst.SharedStatic<NativeArray<float3>>`, because the Svelto range-task
prototype is copied into the collection once at `Add()` and cannot be refreshed
per frame.

Engine constraints that shaped this design (verified against Unity 6000.5
docs and the Unity Discussions thread with DOTS rendering engineers):

- `SubUpdates` is explicitly UNSYNCHRONIZED: overwriting a region the GPU is
  still reading is undefined behavior. All CPU examples intentionally have no
  explicit completion gate; they are throughput comparisons, not production
  synchronization patterns.
- One `BeginWrite` per upload cycle per buffer; write full linear ranges
  (write-combined memory); never read the region on the CPU.
- The region points to real GPU memory only on APIs supporting persistent
  mapping (DX12/Vulkan); otherwise Unity returns temporary CPU memory. The
  documented fewer-copy guarantee still applies, while the size of the win is
  backend-dependent.
- The shader must read through a non-RW `StructuredBuffer` on DX11
  (`Custom/MillionPointsCPU` does exactly this).

`MillionPoints.unity` contains the Svelto and Unity Jobs comparison components.

### Unity Jobs CPU reference

`UnityJobsKernel/MillionPointsCPUUnityJobs.cs` owns persistent input data and
schedules `UnityJobsKernel/ParticlesCPUKernel.cs` every frame. Each job writes
directly to that frame's mapped position region, then the main thread completes
the `JobHandle`, closes the write, and draws. It uploads static albedo to
`_AlbedoBuffer` during initialization. Its particle structs use
`Unity.Mathematics.float3`, which retains the three-float layout required by
both compute buffers.

### GPU compute implementation

`ComputeShadersKernel/MillionPointsGPU.cs` initializes one structured particle
buffer and binds it to both the compute shader as `_CubeDataBuffer` and the
render material as `_ParticleDataBuffer`. Each frame it supplies `_time`,
dispatches `MainCS`, and issues the indirect draw.

`ComputeShadersKernel/MillionPoints.compute` uses 256 threads per group. The
C# side sets `_particleCount` and dispatches `ceil(particleCount / 256)`
groups; `MainCS` must return before accessing the buffer when its dispatched
index is outside that count. This supports the partial final group without
accessing out-of-range particle data.

## Package navigation

The task scheduler implementation is under:

```
Packages/com.sebaslab.svelto.tasks/Svelto.Tasks/
```

Key areas are `Runners/`, `Parallelism/`, and `Tasks/ExtraLean/`. Package tests
live under `Packages/com.sebaslab.svelto.tasks/Svelto.Tasks.Tests~/`.

## Verification paths

- Unity C# and shader compilation, scene serialization, and runtime rendering:
  verify through Unity MCP first. Use the [Unity Hub CLI](https://docs.unity.com/en-us/hub/unity-cli)
  only when MCP is unavailable or lacks the required operation. Unity CLI here
  specifically means the Unity Hub CLI, not a direct Unity Editor command-line
  invocation. Discover the connected Editor and its available commands before
   issuing a verification command.
- Startup menu changes: verify the Build Settings order, capture the startup
  menu in Play mode, confirm that it excludes `Startup`, and load each listed
  target scene through Unity MCP.
- Package tests: run the test command configured by the relevant package test
  project. This is package-level testing, not a substitute for Unity Editor
  compilation.

When changing MillionPoints, preserve the CPU shader's separate position and
albedo buffer layout, the compute implementation's independent particle record,
buffer property names, and indirect-instancing argument layout unless every
producer and consumer is intentionally updated together.
