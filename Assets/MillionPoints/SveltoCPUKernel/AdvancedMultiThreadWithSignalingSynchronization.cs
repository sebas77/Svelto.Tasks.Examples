using System.Collections;
using System.Threading;
using Svelto.Tasks;
using Svelto.Tasks.Enumerators;
using Svelto.Tasks.ExtraLean;
using UnityEngine;

namespace Svelto.Tasks.Example.MillionPoints.Multithreading
{
    //
    // Most advanced scenario to synchronize two different thread
    //

    public class MainThreadSignal : WaitForSignal<MainThreadSignal>
    {
        public MainThreadSignal(string name, float timeout = 1000) : base(name, timeout) { }
    }

    public class OtherThreadSignal : WaitForSignal<OtherThreadSignal>
    {
        public OtherThreadSignal(string name, float timeout = 1000) : base(name, timeout) { }
    }

    public partial class MillionPointsCPU
    {
        IEnumerator SignalBasedAdvancedMultithreadYielding()
        {
            var bounds = new Bounds(_BoundCenter, _BoundSize);

            //these will help with synchronization between threads
            MainThreadSignal mainWaitForSignal = new MainThreadSignal("MainThreadWait", 1000);
            OtherThreadSignal otherwaitForSignal = new OtherThreadSignal("OtherThreadWait", 1000);

            //Start the operations on other threads
            OperationsRunningOnOtherThreads(mainWaitForSignal, otherwaitForSignal)
                .RunOn(_multiThreadRunner);

            //start the main thread loop
            while (true)
            {
                _time = Time.time / 10;

                //Since we want to feed the GPU with the data processed 
                //from the other thread, we can't set the particleDataBuffer
                //until this operation is done. For this reason we stall
                //the mainthread until the data is ready. This operation is advanced
                //as it could stall the game for ever if you don't know
                //what you are doing! 
                otherwaitForSignal.Wait().Complete();
                _pc.particlesTransformed = 0;
                _particleDataBuffer.SetData(_gpuparticleDataArr);

                //tell to the other thread that now it can perform the operations
                //for the next frame.
                mainWaitForSignal.Signal();

                //do something seriously slow
#if DO_SOMETHING_SERIOUSLY_SLOW            
                Thread.Sleep(10);
#endif    
                //render the particles. I use DrawMeshInstancedIndirect but
                //there aren't any compute shaders running. This is so cool!
                Graphics.DrawMeshInstancedIndirect(_pointMesh, 0, _material,
                    bounds, _GPUInstancingArgsBuffer);

                //continue the cycle on the next frame
                yield return null;
            }
        }

        IEnumerator OperationsRunningOnOtherThreads(MainThreadSignal mainWaitForSignal,
                                                    OtherThreadSignal otherWaitForSignal)
        {
            while (true)
            {
                //execute the tasks. The MultiParallelTask is a special collection
                //that uses N threads on its own to execute the tasks. The
                //complete operation is similar to the Unity Jobs complete 
                //operations. It stalls the thread where it's called from
                //until everything is done!
                yield return _multiParallelTasks;
                //the 1 Million particles operation are done, let's signal that the
                //result can now be used
                otherWaitForSignal.Signal();
                //yield until the application is over or the main thread will tell
                //us that now we can perform again the particles operation.
                //since we are not using the thread for anything else
                //we can stall the thread here until is done
                yield return mainWaitForSignal.Wait();
            }
        }
    }
}