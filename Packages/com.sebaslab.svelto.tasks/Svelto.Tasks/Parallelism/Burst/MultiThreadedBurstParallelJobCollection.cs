#if SVELTO_BURST
using Svelto.Tasks.Parallelism;
using Svelto.Tasks.Parallelism.Internal;

/// <summary>
/// a ParallelTaskCollection ran by MultiThreadRunner will run the tasks in a single thread
/// MultiThreadParallelTaskCollection enables parallel tasks to run on different threads.
/// This Burst variant splits a job across ranges and executes each range inside a single
/// Burst-compiled native loop, so the work is compiled (not just scheduled) by Burst.
/// </summary>
public class
        MultiThreadedBurstParallelJobCollection<TJob> :  Svelto.Tasks.Parallelism.ExtraLean.MultiThreadedParallelTaskCollection<BurstParallelEnumerator<TJob>>
            where TJob : unmanaged, ISveltoJob
{
    //works similarly to Unity Jobs, the same job is split into different iterations, the work is then divided according to the indexed iterations
    public void Add(in TJob job, int iterations)
    {
        if (isRunning == true)
            throw new MultiThreadedParallelTaskCollectionException(
                "can't add tasks on a started MultiThreadedParallelTaskCollection");

        var runnersLength   = _runners.Length;
        int tasksPerThread     = (int)System.MathF.Floor((float)iterations / runnersLength);
        int reminder           = iterations % runnersLength;

        for (int i = 0; i < runnersLength; i++)
            Add(new BurstParallelEnumerator<TJob>(job, tasksPerThread * i, tasksPerThread));

        if (reminder > 0)
            Add(new BurstParallelEnumerator<TJob>(job, tasksPerThread * runnersLength, reminder));
    }

    public MultiThreadedBurstParallelJobCollection(string name, uint numberOfThreads, bool tightTasks) : base(name,
        numberOfThreads, tightTasks)
    {
    }
}
#endif