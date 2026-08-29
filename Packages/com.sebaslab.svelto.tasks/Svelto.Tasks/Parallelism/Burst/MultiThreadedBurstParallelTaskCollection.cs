#if SVELTO_BURST
using System;
using Svelto.Tasks.Parallelism;

/// <summary>
/// Splits a concrete Burst range task into fixed-size work-stealing segments.
/// The collection performs managed scheduling only. Each concrete task owns the
/// statically-known call to its non-generic Burst entry point.
/// </summary>
public sealed class MultiThreadedBurstParallelTaskCollection<TTask> :
    Svelto.Tasks.Parallelism.ExtraLean.MultiThreadedParallelTaskCollection<TTask>
    where TTask : unmanaged, IBurstParallelTask
{
    public MultiThreadedBurstParallelTaskCollection(string name, uint numberOfThreads, bool tightTasks) :
        base(name, numberOfThreads, tightTasks)
    {
    }

    public void Add(in TTask prototype, int iterations, int elementsPerTask)
    {
        if (isRunning == true)
            throw new MultiThreadedParallelTaskCollectionException(
                "can't add tasks on a started MultiThreadedParallelTaskCollection");
        if (elementsPerTask <= 0)
            throw new ArgumentOutOfRangeException(nameof(elementsPerTask));

        //the last segment absorbs the division remainder so no iteration is left out
        for (int start = 0; start < iterations; start += elementsPerTask)
        {
            TTask rangeTask = prototype;
            rangeTask.SetRange(start, Math.Min(elementsPerTask, iterations - start));
            base.Add(rangeTask);
        }
    }
}
#endif
