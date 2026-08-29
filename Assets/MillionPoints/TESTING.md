# MillionPoints Manual Verification

The MillionPoints scene contains several implementations on one GameObject.
`RuntimeImplementationSelector` enables exactly one implementation at runtime;
entering Play mode alone does **not** prove that a particular implementation
ran.

## Implementation selector

Select the implementation explicitly with the scene buttons, or use the `[` and
`]` keys to move backward and forward through the selector.

The fixed selector order is:

| Index | Component |
| --- | --- |
| 0 | `MillionPointsCPU_BurstSync` |
| 1 | `MillionPointsCPUUnityJobs` |
| 2 | `MillionPointsCPU_AdvancedSync` |
| 3 | `MillionPointsCPU_IndependentThreads` |
| 4 | `MillionPointsGPU` |

When testing through Unity MCP, select a component only after entering Play
mode. `SwitchTo` disables the previous component before enabling the next one,
so the previous implementation's `OnDisable` cleanup is part of the test.

```csharp
var selector = Object.FindFirstObjectByType<RuntimeImplementationSelector>();
selector.SwitchTo(0); // BurstSync
```

Replace `0` with the required index from the table.

## CPU upload fence test procedure

Use this procedure for `MillionPointsCPU_BurstSync`,
`MillionPointsCPUUnityJobs`, `MillionPointsCPU_AdvancedSync`, and
`MillionPointsCPU_IndependentThreads`.

1. Ensure Unity has finished compiling and the editor is ready.
2. Enter Play mode.
3. Explicitly select the implementation under test.
4. Clear the Console **after** selection so startup and shutdown messages from
   another implementation do not contaminate the result.
5. Let the selected implementation run for several seconds.
6. Inspect the Console for errors and warnings.
7. Select the next implementation and repeat from step 4.
8. Exit Play mode and inspect the Console once more for cleanup errors.

The test passes when the selected implementation runs and switches away without
any of the following:

- `GraphicsFence` creation or `passed` exceptions;
- `BeginWrite` / `EndWrite` errors;
- `WaitForSignal` timeouts;
- disposed native-container or compute-buffer errors;
- worker-runner shutdown errors.

## Fence ownership model to validate

The CPU upload implementations use two `ComputeBufferMode.SubUpdates` buffers.
Each slot has its own latest graphics fence:

```text
write slot 0 -> draw slot 0 -> fence 0
write slot 1 -> draw slot 1 -> fence 1
reuse slot 0 only after fence 0 passes
reuse slot 1 only after fence 1 passes
```

If the fence for the next upload slot is pending:

- `BurstSync` and `AdvancedSync` defer only their upload loop and continue
  rendering the other completed slot.
- `UnityJobs` skips that frame's upload and continues rendering its most recent
  completed slot.
- `IndependentThreads` runs a reusable `BeginWrite` task on the update runner,
  writes the mapped slot from its background Burst workers, and lets the main
  render task call `EndWrite` only after the workers publish completion.

All four use `GraphicsFenceType.CPUSynchronisation`: the CPU polls GPU
completion before calling `BeginWrite` on a previously rendered slot. This is
not `AsyncQueueSynchronisation`, which coordinates graphics and asynchronous
GPU-compute queues and requires async-compute capability when polled.

For IndependentThreads, also verify that the upload ownership state repeats in
this order without timeouts or invalid buffer access:

```text
Closed -> BeginWrite -> Computing -> workers complete -> ReadyToClose
   ^                                                     |
   +----------------------- EndWrite --------------------+
```

## Unity MCP limitation

Million-particle runs can make the Unity MCP bridge temporarily stop answering
pings. A disconnected bridge is not a test result.

When that happens:

1. Stop Play mode manually if necessary.
2. Wait for the Editor and MCP bridge to reconnect.
3. Start a new, explicitly selected test run.

Do not infer that an implementation passed merely because Play mode was entered
or because a component happened to be initially enabled.
