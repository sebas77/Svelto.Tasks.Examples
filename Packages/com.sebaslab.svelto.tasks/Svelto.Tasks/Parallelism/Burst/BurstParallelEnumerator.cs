#if SVELTO_BURST
using System.Runtime.CompilerServices;
using Unity.Burst;

namespace Svelto.Tasks.Parallelism.Internal
{
    /// <summary>
    /// Burst-compiled parallel enumerator. It splits a job across a range of indices, executing the whole
    /// range inside a single Burst-compiled native loop, so it is not just as fast as Unity Jobs: it uses
    /// the same compiler.
    /// The job type must be unmanaged (no reference type fields) so that it is blittable and Burst-compatible.
    /// </summary>
    public struct BurstParallelEnumerator<T> : IParallelTask
        where T : unmanaged, ISveltoJob
    {
        public BurstParallelEnumerator(in T job, int startIndex, int numberOfIterations)
        {
            _startIndex = startIndex;
            _numberOfIterations = numberOfIterations;
            _job = job;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            BurstLoop(ref _job, _startIndex, _numberOfIterations);

            return false;
        }

        public void Reset()
        {}

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            _job.Dispose();
        }

        public object Current => null;

        // the whole iteration range is processed inside a single Burst-compiled native loop
        [BurstCompile]
        public static void BurstLoop(ref T job, int startIndex, int numberOfIterations)
        {
            for (int i = 0; i < numberOfIterations; i++)
                job.Update(startIndex + i);
        }

        T _job;
        readonly int _startIndex;
        readonly int _numberOfIterations;
    }
}
#endif