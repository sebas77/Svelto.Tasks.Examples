# Labyrinth Open Runner Pool Plan

## Goal

Add a new maze-solving component that dynamically schedules one task for each branch across an open pool of `MultiThreadRunner` instances.

The existing `Assets/Labyrinth/Labyrinth.cs` example must remain unchanged and working. The new example will not paint while searching. When the search ends, it will paint every explored node in one color and then paint the winning path over it in a second color.

## 1. ExtraLean Runner Pool

Add `MultiThreadRunnerPool` to the embedded Svelto.Tasks package under:

`Packages/com.sebaslab.svelto.tasks/Svelto.Tasks/Runners/MultiThreadRunnerPool.cs`

Place it in the `Svelto.Tasks.ExtraLean` namespace and implement:

```csharp
IRunner<ExtraLeanSveltoTask<IEnumerator>>
```

The pool will:

- Own an array of `ExtraLean.MultiThreadRunner` instances.
- Dispatch root tasks round-robin using `Interlocked.Increment`.
- Use `Math.Max(1, Environment.ProcessorCount - 2)` as its default worker count, matching the existing Svelto.Tasks parallel collection convention.
- Accept an explicit worker count so the example can override the default when required.
- Expose `Stop()` to request a graceful stop from every inner runner without blocking the calling worker.
- Expose an idempotent `Dispose()` that disposes every inner runner.
- Allow `Stop()` to be called from a worker, but require `Dispose()` to be called externally rather than from a pool worker.

This pool is specifically for independently scheduled ExtraLean root tasks. Runner-local continuation indices cannot safely be transferred between different inner runners.

Add package tests covering:

- Round-robin dispatch across multiple worker threads.
- Dynamic scheduling while the pool is already running.
- Pool-wide stop forwarding.
- Task cleanup and idempotent disposal.

## 2. New Example Component

Add:

`Assets/Labyrinth/LabyrinthPoolSearch.cs`

This will be a separate, self-contained `MonoBehaviour`. It will reuse the existing public `Graph` and `GraphEdge` types and copy only the private texture, grid construction, start/goal selection, and painting helpers it needs.

Do not modify `Assets/Labyrinth/Labyrinth.cs`. The two search components should not run against the same renderer at the same time.

The component will expose:

- An optional worker-count override, with the pool default used when unset.
- An explored-path color.
- A winning-path color.
- The existing texture/grid and paint-radius settings.

## 3. Shared Visited State and Path History

Use one shared `int[] predecessors` array instead of a `ConcurrentDictionary`.

The graph has a fixed, dense node range, so an array is cheaper and also records the winning path without another data structure.

Use these sentinel values:

```csharp
const int Unvisited = -2;
const int NoParent = -1;
```

Initialize every entry to `Unvisited`, then initialize the start node to `NoParent` before scheduling the root task.

Claim a node and record its parent atomically:

```csharp
Interlocked.CompareExchange(
    ref predecessors[node],
    parent,
    Unvisited) == Unvisited;
```

This operation:

- Marks the node globally visited.
- Ensures only one branch can claim it.
- Prevents cycles and duplicate exploration.
- Records the predecessor needed to reconstruct the winning path.

## 4. Branch Ownership

Every scheduled branch receives a starting node that has already been claimed. The child must not claim that starting node again.

The branch algorithm is:

```text
SearchBranch(node):
    while the search is not solved:
        if node is the goal:
            atomically become the single winner
            ask every runner in the pool to stop
            publish that stop has been requested
            end this task

        atomically claim each currently unvisited neighbour,
        storing node as that neighbour's predecessor

        if no neighbour was claimed:
            end this task

        if exactly one neighbour was claimed:
            continue this task through that neighbour
            yield once for cooperative scheduling

        if multiple neighbours were claimed:
            schedule one child task for every claimed neighbour
            end the parent task
```

At a true fork, every continuation becomes a child and the parent has no remaining responsibility.

If concurrent work has already claimed all but one neighbour, the current task continues through the one neighbour it successfully claimed instead of creating a redundant fork.

## 5. Stop and Disposal Lifecycle

`MultiThreadRunner.Stop()` stops currently running tasks on their next step, but queued tasks can remain in a runner queue. The solve lifecycle must therefore be two-phase:

1. The first task reaching the goal atomically sets the solved flag.
2. That winning worker calls `_pool.Stop()` so every inner runner receives a stop request.
3. After all stop requests have been issued, the worker publishes a separate stop-requested flag.
4. Unity's main thread observes that flag in `Update()`.
5. The main thread calls `_pool.Dispose()` to clean up running and queued tasks and wait for worker shutdown.
6. Painting begins only after disposal returns, so no worker can still modify search state.

Calling `Dispose()` directly from the winning worker is not allowed because the runner can wait for the worker that is currently executing the call.

`OnDestroy()` will also dispose the pool so scene changes and early component destruction clean up all worker threads.

## 6. Natural Exhaustion

Track an atomic active-branch count for the no-solution case:

- Increment it before scheduling a branch.
- Decrement it in the branch iterator's `finally` block.
- When it reaches zero without a solution, report that no path was found and dispose the pool from the main thread.

The counter is only used for natural exhaustion. After a solution, disposal can remove queued iterators before they start, so completion after a solution is controlled by the stop-requested flag instead.

## 7. Winning Path Reconstruction

After the pool has been disposed, reconstruct the winning path on the main thread by following predecessors backward:

```text
goal -> predecessors[goal] -> ... -> start
```

Reverse the resulting sequence if start-to-goal order is needed.

Because a node can only be claimed once, its predecessor never changes after a successful claim.

## 8. Final Painting

All Unity texture operations remain on Unity's main thread.

Paint exactly once after search shutdown:

1. Paint every node whose predecessor is not `Unvisited` using the explored-path color.
2. Paint every node in the reconstructed winning path using the winning-path color, on top of the explored color.
3. Call `ApplyPaint()` once.

If no solution exists, paint the explored nodes and log that no path was found; there will be no winning-path overlay.

## 9. Verification

Before considering the implementation complete:

- Run the Svelto.Tasks pool tests.
- Compile the Unity project and confirm both maze components compile.
- Run the existing `Labyrinth` component to confirm its behavior is unchanged.
- Run `LabyrinthPoolSearch` and confirm multiple worker threads participate.
- Confirm each node is globally claimed at most once.
- Confirm all parent tasks end after a true fork.
- Confirm goal discovery requests stop on every runner.
- Confirm disposal occurs on the main thread and no worker survives component destruction.
- Confirm painting occurs only once and the winning path is painted over the explored paths.
