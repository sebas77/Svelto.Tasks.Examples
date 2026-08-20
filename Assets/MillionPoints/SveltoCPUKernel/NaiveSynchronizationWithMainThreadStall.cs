using System.Collections;
using UnityEngine;

namespace Svelto.Tasks.Example.MillionPoints.Multithreading
{
    //
    // Naive synchronization, like it happens with the Unity Jobs
    //
    public partial class MillionPointsCPU
    {
        IEnumerator MainThreadLoopWithNaiveSynchronization()
        {
            var bounds = new Bounds(_BoundCenter, _BoundSize);

            while (true)
            {
                _time = Time.time / 10;
                //Since we are using the SyncRunner, we don't need to yield the execution
                //as the SyncRunner is meant to stall the thread where it starts from.
                //The main thread will be stuck until the multiParallelTask has been
                //executed. A MultiParallelTaskCollection relies on its own
                //internal threads to run, so although the Main thread is stuck
                //the operation will complete
                _multiParallelTasks.Complete();
                //then it resumes here, in the main thread, copying the result to the particleDataBuffer.
                //remember, multiParalleTasks is not executing anymore until the next frame!
                //so the array is safe to use
                _particleDataBuffer.SetData(_gpuparticleDataArr);
                //render the particles. I use DrawMeshInstancedIndirect but
                //there aren't any compute shaders running. This is so cool!
                Graphics.DrawMeshInstancedIndirect(_pointMesh, 0, _material,
                    bounds, _GPUInstancingArgsBuffer);
                //continue the cycle on the next frame
                yield return null;
            }
        }
    }
}