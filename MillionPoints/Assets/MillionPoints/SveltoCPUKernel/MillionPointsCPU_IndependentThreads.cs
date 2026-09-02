using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Svelto.DataStructures;
using Svelto.Tasks;
using Svelto.Tasks.Enumerators;
using Svelto.Tasks.Lean;
using Svelto.Tasks.Parallelism.ExtraLean;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;

namespace Svelto.Tasks.Example.MillionPoints.Multithreading
{
    // Independent runner pipeline. A reusable main-thread task opens a fence-safe mapped
    // upload slot, then the coordinator launches Burst workers that write into it directly.
    // The render root closes completed writes and draws the latest published slot.
    public class MillionPointsCPU_IndependentThreads : MonoBehaviour
    {
        [TextArea] public string Notes =
            "Independent threads strategy (Burst): a main-thread task opens a fence-safe " +
            "mapped upload slot, background Burst tasks write into it directly, and the " +
            "render task closes completed writes before drawing the latest slot.";

        [SerializeField] uint _particleCount;
        [SerializeField] Material _material;
        [SerializeField] Shader _shader;
        [SerializeField] Vector3 _BoundCenter = Vector3.zero;
        [SerializeField] Vector3 _BoundSize = new Vector3(300f, 300f, 300f);
        [SerializeField, Min(1)] int _elementsPerTask = 8192;

        void Awake()
        {
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
        }

        void OnEnable()
        {
            Volatile.Write(ref _stopping, false);
            Volatile.Write(ref _uploadState, UploadClosed);
            _frameIndex = 0;
            _hasCompletedRenderBuffer = false;
            _updateRunner = new SteppableRunner("MillionPoints.IndependentThreads.Update");
            _multiThreadRunner = new MultiThreadRunner("MillionPoints.IndependentThreads.Coordinator");

            InitializeParticleData();
            InitializeRendering();
            InitializeTasks();

            _beginWriteTask = new BeginWriteTask(this);

            //These roots intentionally start independently. The renderer advances every
            //Unity Update; the coordinator requests mapped slots through the update runner.
            RenderAndUploadOnMainThread().RunOn(_updateRunner);
            WorkerLoop().RunOn(_multiThreadRunner);
        }

        void Update()
        {
            _updateRunner.Step();
        }

        void OnDisable()
        {
            Volatile.Write(ref _stopping, true);
            _multiThreadRunner?.Dispose();

            if (_particleTasks != null)
            {
                if (_particleTasks.isRunning)
                    _particleTasks.Complete();

                EndWriteIfOpen();

                _particleTasks.Dispose();
                _particleTasks = null;
            }

            _updateRunner?.Dispose();
            _updateRunner = null;
            _multiThreadRunner = null;
            _beginWriteTask = null;
            _latestRenderFences = null;
            _hasRenderFence = null;

            if (_frameData.isValid)
                _frameData.Dispose();

            if (_cpuParticles.isValid)
                _cpuParticles.Dispose();

            if (_uploadBuffers != null)
            {
                for (int i = 0; i < _uploadBuffers.Length; i++)
                {
                    if (_uploadBuffers[i] != null)
                    {
                        _uploadBuffers[i].Release();
                        _uploadBuffers[i] = null;
                    }
                }
            }

            if (_albedoBuffer != null)
            {
                _albedoBuffer.Release();
                _albedoBuffer = null;
            }

            if (_GPUInstancingArgsBuffer != null)
            {
                _GPUInstancingArgsBuffer.Release();
                _GPUInstancingArgsBuffer = null;
            }

            if (_pointMesh != null)
            {
                Destroy(_pointMesh);
                _pointMesh = null;
            }
        }

        void InitializeParticleData()
        {
            _cpuParticles = NativeDynamicArray.Alloc<IndependentThreadsBurstParticleData>(
                Svelto.Common.Allocator.Persistent, _particleCount);
            _frameData = NativeDynamicArray.Alloc<IndependentThreadsFrameData>(
                Svelto.Common.Allocator.Persistent, 1);

            var albedos = new float3[(int) _particleCount];
            for (uint index = 0; index < _particleCount; index++)
            {
                _cpuParticles.Set(index, new IndependentThreadsBurstParticleData(
                    new float3(Random.Range(-10.0f, 10.0f), Random.Range(-10.0f, 10.0f),
                               Random.Range(-10.0f, 10.0f)), Random.Range(1.0f, 100.0f)));
                albedos[(int) index] = new float3(
                    Random.Range(0.0f, 1.0f), Random.Range(0.0f, 1.0f), Random.Range(0.0f, 1.0f));
            }

            _albedoBuffer = new ComputeBuffer((int) _particleCount, sizeof(float) * 3);
            _albedoBuffer.SetData(albedos);
        }

        void InitializeRendering()
        {
            _uploadBuffers = new ComputeBuffer[2];
            for (int i = 0; i < _uploadBuffers.Length; i++)
                _uploadBuffers[i] = new ComputeBuffer((int) _particleCount, sizeof(float) * 3,
                    ComputeBufferType.Structured, ComputeBufferMode.SubUpdates);

            _latestRenderFences = new GraphicsFence[_uploadBuffers.Length];
            _hasRenderFence = new bool[_uploadBuffers.Length];

            _pointMesh = new Mesh
            {
                vertices = new[] {new Vector3(0, 0)}
            };
            _pointMesh.SetIndices(new[] {0}, MeshTopology.Points, 0);

            _GPUInstancingArgsBuffer = new ComputeBuffer(
                1, _GPUInstancingArgs.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
            _GPUInstancingArgs[0] = _pointMesh.GetIndexCount(0);
            _GPUInstancingArgs[1] = _particleCount;
            _GPUInstancingArgsBuffer.SetData(_GPUInstancingArgs);

            _activeRenderBuffer = _uploadBuffers[0];
            _activeRenderSlot = 0;
            _material.shader = _shader;
            _material.SetBuffer("_ParticleDataBuffer", _activeRenderBuffer);
            _material.SetBuffer("_AlbedoBuffer", _albedoBuffer);
        }

        void InitializeTasks()
        {
            uint workerCount = (uint) Math.Max(1, Environment.ProcessorCount - 1);

            //Force Burst direct-call compilation during setup instead of the first measured pass.
            NativeArray<float3> emptyRegion = default;
            MillionPointsIndependentThreadsBurstKernel.Execute(
                ref _cpuParticles, ref emptyRegion, 0f, 0, 0);

            _particleTasks =
                new MultiThreadedBurstParallelTaskCollection<IndependentThreadsBurstRangeTask>(
                    "MillionPoints.IndependentThreads", workerCount, true);
            var prototype = new IndependentThreadsBurstRangeTask(_cpuParticles, _frameData);
            _particleTasks.Add(in prototype, (int) _particleCount, _elementsPerTask);
        }

        //Cross-thread ownership and double-buffer cycle:
        //
        //  Coordinator                 Update runner                  GPU queue
        //      |                            |                              |
        //      |-- RunOn(BeginWrite) ----->|                              |
        //      |                            | wait latestFence[slot]       |
        //      |                            | BeginWrite(slot)             |
        //      |<-- continuation done ------|                              |
        //      |                            |                              |
        //      |-- Burst workers write mapped slot                        |
        //      |-- state = READY_TO_CLOSE                                 |
        //      |                            |                              |
        //      |                            | EndWrite(slot)               |
        //      |                            | publish + Draw(slot) ------->|
        //      |                            | CreateFence(slot) ---------->|
        //      |                            |                              |
        //      +-- immediately requests the other slot; its BeginWriteTask waits until
        //          the previous mapping is CLOSED and that slot's newest fence has passed.
        //
        //  CLOSED --BeginWrite--> COMPUTING --workers done--> READY_TO_CLOSE
        //     ^                                                   |
        //     +----------------------- EndWrite ------------------+
        //
        //Slots alternate with _frameIndex & 1. RenderAndUpload may draw one completed slot
        //several times while workers fill the other, replacing only that slot's latest fence.
        IEnumerator<TaskContract> WorkerLoop()
        {
            var then = DateTime.Now;

            while (stopping == false)
            {
                Volatile.Write(ref _requestedTime, (float) (DateTime.Now - then).TotalSeconds);

                //This continuation may wait across Updates for slot ownership, but completes
                //in the same MoveNext that calls BeginWrite. The coordinator can therefore
                //launch the Burst pass without waiting for the rest of that Unity frame.
                yield return _beginWriteTask.RunOn(_updateRunner);

                //The reusable collection represents one complete computation pass. Yielding
                //its run handle suspends this coordinator until every Burst range has written
                //its disjoint portion of the mapped upload region.
                yield return _particleTasks.Run().Continue();

                //Release-publish every worker write to the main thread. EndWrite is legal only
                //after this transition; an open mapping alone does not mean computation ended.
                Volatile.Write(ref _uploadState, UploadReadyToClose);
            }
        }

        //The main runner advances every frame. It closes a mapped slot only after the worker
        //publishes UploadReadyToClose, then renders that completed slot independently while
        //the coordinator requests and computes the next one.
        IEnumerator<TaskContract> RenderAndUploadOnMainThread()
        {
            var bounds = new Bounds(_BoundCenter, _BoundSize);

            while (stopping == false)
            {
                if (Volatile.Read(ref _uploadState) == UploadReadyToClose)
                    PublishCompletedWrite();

                if (_hasCompletedRenderBuffer)
                {
                    _material.SetBuffer("_ParticleDataBuffer", _activeRenderBuffer);
                    Graphics.DrawMeshInstancedIndirect(
                        _pointMesh, 0, _material, bounds, _GPUInstancingArgsBuffer);

                    //DrawMeshInstancedIndirect only enqueued the draw on the GPU command queue and
                    //returned immediately: the GPU may still be reading the particle buffer right
                    //now. CreateGraphicsFence appends a fence command after the draw; with
                    //CPUSynchronisation + AllGPUOperations it signals (fence.passed becomes true)
                    //only once ALL GPU work queued so far, including that draw, has completed.
                    //Until then this slot is GPU-owned: the upload loop polls the fence before
                    //mapping it for writing again.
                    _latestRenderFences[_activeRenderSlot] = Graphics.CreateGraphicsFence(
                        GraphicsFenceType.CPUSynchronisation,
                        SynchronisationStageFlags.AllGPUOperations);
                    _hasRenderFence[_activeRenderSlot] = true;
                }

                yield return TaskContract.Yield.It;
            }
        }

        void PublishCompletedWrite()
        {
            ComputeBuffer completedBuffer = _openBuffer;
            int completedSlot = _openSlot;

            completedBuffer.EndWrite<float3>((int) _particleCount);
            _frameData.Get<IndependentThreadsFrameData>(0).uploadRegion = default;
            _openBuffer = null;

            _activeRenderBuffer = completedBuffer;
            _activeRenderSlot = completedSlot;
            _hasCompletedRenderBuffer = true;

            //Release the mapping before advertising Closed: BeginWriteTask may be queued on
            //this same runner and can open the other slot as soon as it observes this state.
            Volatile.Write(ref _uploadState, UploadClosed);
        }

        void EndWriteIfOpen()
        {
            if (Volatile.Read(ref _uploadState) == UploadClosed)
                return;

            _openBuffer.EndWrite<float3>((int) _particleCount);
            _frameData.Get<IndependentThreadsFrameData>(0).uploadRegion = default;
            _openBuffer = null;
            Volatile.Write(ref _uploadState, UploadClosed);
        }

        //Reusable main-thread gate. It completes in the same call that opens a mapped slot.
        //Until then it yields if the previous pass has not been closed or if the next slot's
        //latest draw fence still gives ownership to the GPU.
        sealed class BeginWriteTask : IEnumerator<TaskContract>
        {
            internal BeginWriteTask(MillionPointsCPU_IndependentThreads owner)
            {
                _owner = owner;
            }

            public bool MoveNext()
            {
                if (Volatile.Read(ref _owner._uploadState) != UploadClosed)
                    return true;

                int slot = _owner._frameIndex & 1;

                if (_owner._hasRenderFence[slot] &&
                    _owner._latestRenderFences[slot].passed == false)
                    return true;

                ComputeBuffer buffer = _owner._uploadBuffers[slot];
                _owner._hasRenderFence[slot] = false;

                NativeArray<float3> uploadRegion =
                    buffer.BeginWrite<float3>(0, (int) _owner._particleCount);

                _owner._openBuffer = buffer;
                _owner._openSlot = slot;
                _owner._frameData.Set(0, new IndependentThreadsFrameData(
                    uploadRegion, Volatile.Read(ref _owner._requestedTime)));
                _owner._frameIndex++;

                //The continuation completion publishes frameData to the coordinator; the
                //state records that only the Burst workers, not the renderer, own this map.
                Volatile.Write(ref _owner._uploadState, UploadComputing);
                return false;
            }

            public void Reset() { }
            public void Dispose() { }
            public TaskContract Current => TaskContract.Yield.It;
            object IEnumerator.Current => Current;
            public override string ToString() => "IndependentThreads.BeginWrite";

            readonly MillionPointsCPU_IndependentThreads _owner;
        }

        //DrawMeshInstancedIndirect reads a fixed 5-uint argument block from the
        //IndirectArguments buffer: {index count per instance, instance count, start index,
        //base vertex, start instance}. Only the first two are meaningful here (1 point per
        //instance, _particleCount instances); the rest stay 0 but must exist because the GPU
        //always consumes the full 20-byte block.
        readonly uint[] _GPUInstancingArgs = {0, 0, 0, 0, 0};

        const int UploadClosed = 0;
        const int UploadComputing = 1;
        const int UploadReadyToClose = 2;

        //Double-buffered particle positions: the CPU writes one slot through BeginWrite/EndWrite
        //while the GPU renders the other. Indexed by _frameIndex & 1.
        ComputeBuffer[] _uploadBuffers;
        //Slot currently mapped for CPU writing (BeginWrite opened, EndWrite still pending)
        ComputeBuffer _openBuffer;
        //Last fully written slot, currently bound to the material for rendering
        ComputeBuffer _activeRenderBuffer;
        //Static per-particle colors, uploaded once at startup
        ComputeBuffer _albedoBuffer;
        //Indirect-arguments buffer for DrawMeshInstancedIndirect (index count, instance count, ...)
        ComputeBuffer _GPUInstancingArgsBuffer;
        Mesh _pointMesh;
        //Latest GraphicsFence issued after drawing each slot; .passed means the GPU has
        //finished reading that slot and the CPU may map it for writing again
        GraphicsFence[] _latestRenderFences;
        //True when a fence is pending for that slot (the draw may still be in flight on the GPU);
        //cleared once the fence passes and the slot is safe to reuse
        bool[] _hasRenderFence;

        NativeDynamicArray _cpuParticles;
        NativeDynamicArray _frameData;
        MultiThreadedBurstParallelTaskCollection<IndependentThreadsBurstRangeTask> _particleTasks;

        SteppableRunner _updateRunner;
        MultiThreadRunner _multiThreadRunner;
        BeginWriteTask _beginWriteTask;
        int _frameIndex;
        int _openSlot;
        int _activeRenderSlot;
        int _uploadState;
        float _requestedTime;
        bool _stopping;
        bool _hasCompletedRenderBuffer;

        bool stopping => Volatile.Read(ref _stopping);
    }
}
