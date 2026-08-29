using System;
using System.Collections.Generic;
using System.Threading;
using Svelto.DataStructures;
using Svelto.Tasks;
using Svelto.Tasks.Enumerators;
using Svelto.Tasks.Lean;
using Svelto.Tasks.Parallelism;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;

namespace Svelto.Tasks.Example.MillionPoints.Multithreading
{
    // Signal-gated direct write. BeginWrite/EndWrite must run on the main thread, so the
    // per-pass handshake moves exactly those boundaries: the coordinator requests a mapped
    // slot, the main thread maps it and hands the region over, the Burst tasks fill it, and
    // the coordinator signals back when the workers are done so the main thread can close
    // the write and draw. Two GPU buffers alternate every pass so a region is only
    // re-mapped after the draw that reads it has retired. RenderLoop runs every Update.
    public class MillionPointsCPU_AdvancedSync : MonoBehaviour
    {
        [TextArea] public string Notes =
            "Advanced synchronization strategy (Burst): BeginWrite/EndWrite stay on the main " +
            "thread and are gated by a three-signal handshake, while the Burst workers fill " +
            "the mapped GPU region directly. Two upload buffers alternate every pass.";

        [SerializeField] uint _particleCount;
        [SerializeField] Material _material;
        [SerializeField] Shader _shader;
        [SerializeField] Vector3 _BoundCenter = Vector3.zero;
        [SerializeField] Vector3 _BoundSize = new Vector3(300f, 300f, 300f);
        [SerializeField, Min(1)] int _elementsPerTask = 8192;

        public class RegionMappedSignal : WaitForSignal<RegionMappedSignal>
        {
            public RegionMappedSignal(string name, float timeout = 1000) : base(name, timeout) { }
        }

        public class ComputeDoneSignal : WaitForSignal<ComputeDoneSignal>
        {
            public ComputeDoneSignal(string name, float timeout = 1000) : base(name, timeout) { }
        }

        void Awake()
        {
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
        }

        void OnEnable()
        {
            Volatile.Write(ref _stopping, false);
            //Slot 0 is the initial render buffer, so the first upload must target slot 1.
            _frameIndex = 1;
            _updateRunner = new SteppableRunner("MillionPoints.AdvancedSync.Update");
            _multiThreadRunner = new MultiThreadRunner("MillionPoints.AdvancedSync.Coordinator");

            InitializeParticleData();
            InitializeRendering();
            InitializeTasks();

            _regionMapped = new RegionMappedSignal("AdvancedSync.RegionMapped", 1000);
            _computeDone  = new ComputeDoneSignal("AdvancedSync.ComputeDone", 1000);

            //Three independent roots: the update runner hosts the signal-gated upload loop
            //and the unconditional render loop, while the coordinator thread computes.
            UploadLoop().RunOn(_updateRunner);
            RenderLoop().RunOn(_updateRunner);
            ComputeLoop().RunOn(_multiThreadRunner);
        }

        void Update()
        {
            _updateRunner.Step();
        }

        void OnDisable()
        {
            Volatile.Write(ref _stopping, true);

            //WaitForSignal yields cooperatively; disposing the coordinator cancels its
            //parked task. Do not signal RegionMapped here: that signal means a valid mapped
            //region is ready, and waking the coordinator would let it start a new pass.
            _multiThreadRunner?.Dispose();
            _multiThreadRunner = null;

            if (_particleTasks != null)
            {
                if (_particleTasks.isRunning)
                    _particleTasks.Complete();

                _particleTasks.Dispose();
                _particleTasks = null;
            }

            EndWriteIfOpen();

            _updateRunner?.Dispose();
            _updateRunner = null;

            _regionMapped = null;
            _computeDone = null;
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
            _cpuParticles = NativeDynamicArray.Alloc<BurstCPUParticleData>(
                Svelto.Common.Allocator.Persistent, _particleCount);
            _frameData = NativeDynamicArray.Alloc<AdvancedSyncFrameData>(
                Svelto.Common.Allocator.Persistent, 1);

            var albedos = new float3[(int) _particleCount];
            for (uint index = 0; index < _particleCount; index++)
            {
                _cpuParticles.Set(index, new BurstCPUParticleData(
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

            //forces the Burst compile at init: zero iterations, default output region
            NativeArray<float3> emptyRegion = default;
            MillionPointsAdvancedSyncBurstKernel.Execute(
                ref _cpuParticles, ref emptyRegion, 0f, 0, 0);

            _particleTasks =
                new MultiThreadedBurstParallelTaskCollection<AdvancedSyncBurstRangeTask>(
                    "MillionPoints.AdvancedSync", workerCount, true);
            _particleTasks.Add(
                new AdvancedSyncBurstRangeTask(_cpuParticles, _frameData),
                (int) _particleCount, _elementsPerTask);
        }

        //Complete two-slot ownership graph:
        //
        //  _frameIndex & 1 selects the next upload slot. With slot 0 initially rendered,
        //  uploads alternate 1, 0, 1, 0... and never map the currently active render slot.
        //
        //       SLOT 0                                      SLOT 1
        //  +------------------+                        +------------------+
        //  | latestFence[0]   |                        | latestFence[1]   |
        //  | hasFence[0]      |                        | hasFence[1]      |
        //  +--------+---------+                        +--------+---------+
        //           |                                           |
        //           | selected by (_frameIndex & 1)             |
        //           +--------------------+----------------------+
        //                                |
        //                    latestFence[slot].passed?
        //                         |                  |
        //                       yes                  no
        //                         |                  |
        //                         |           yield; RenderLoop keeps
        //                         |           drawing the other slot
        //                         v                  |
        //  CPU: BeginWrite(slot) --regionMapped--> Burst workers
        //         ^                                      |
        //         |                                 computeDone
        //         |                                      v
        //  GPU: fence <--- CreateGraphicsFence <--- Draw <--- EndWrite
        //         ^                                      |
        //         |                                      v
        //  latestFence[slot] = newest fence       activeRenderSlot = slot
        //
        //RenderLoop may draw the active slot repeatedly while the other slot computes. It
        //replaces latestFence[activeRenderSlot] after every draw, retaining only the newest
        //fence for that slot. GPU queue order makes that sufficient: when the newest fence
        //passes, every earlier draw using that slot has also completed. Thus double buffering
        //usually makes the selected fence pass immediately; if it does not, only UploadLoop
        //waits and the renderer continues with the other slot.
        IEnumerator<TaskContract> UploadLoop()
        {
            while (stopping == false)
            {
                int slot = _frameIndex++ & 1;
                ComputeBuffer buffer = _uploadBuffers[slot];

                while (_hasRenderFence[slot] && _latestRenderFences[slot].passed == false)
                    yield return TaskContract.Yield.It;

                //The passed fence transfers this slot from GPU ownership back to the CPU.
                _hasRenderFence[slot] = false;

                NativeArray<float3> uploadRegion =
                    buffer.BeginWrite<float3>(0, (int) _particleCount);
                _openBuffer = buffer;
                _writeOpen = true;

                //publish the mapped region and the frame time for the Burst tasks
                _frameData.Set(0, new AdvancedSyncFrameData(uploadRegion, Time.time));
                _regionMapped.Signal();

                //wait for the workers to finish filling the mapped region
                yield return _computeDone.Wait().Continue();

                buffer.EndWrite<float3>((int) _particleCount);
                _writeOpen = false;

                //Publish the completed slot; its next reuse will pass through the fence gate.
                _activeRenderBuffer = buffer;
                _activeRenderSlot = slot;
            }
        }

        //Renders every Update, unconditionally, drawing whichever upload buffer the upload
        //loop most recently published. Slow compute never gates the render cadence.
        IEnumerator<TaskContract> RenderLoop()
        {
            var bounds = new Bounds(_BoundCenter, _BoundSize);

            while (stopping == false)
            {
                _material.SetBuffer("_ParticleDataBuffer", _activeRenderBuffer);
                Graphics.DrawMeshInstancedIndirect(_pointMesh, 0, _material, bounds, _GPUInstancingArgsBuffer);

                //DrawMeshInstancedIndirect is just a command in a command queue with deferred execution, so we don't know when
                //the GPU has finished reading the _GPUInstancingArgsBuffer. We need a fence to know when it is safe to reuse the buffer.
                //we use double buffering to avoid waiting for the GPU to finish reading the buffer before we can start writing to it again.
                //The fence will be signaled when the GPU has finished reading the buffer,
                //and we can poll it in the UploadLoop to know when it is safe to reuse the buffer.
                //many products actually use triple buffering.
                //CPUSynchronisation:
                //
                //  Graphics queue: Draw(buffer) ---> CPU fence
                //                                         |
                //                                         v
                //                                   CPU polls passed
                //
                //AsyncQueueSynchronisation instead connects two GPU queues:
                //
                //  Graphics queue:      Draw ---> Async fence
                //                                     |
                //                                     v
                //  Async-compute queue:              Wait ---> Compute
                //
                //Polling an AsyncQueueSynchronisation fence also requires
                //supportsAsyncCompute. AdvanceSync has no async-compute GPU queue: it needs
                //the first model, where the CPU regains buffer ownership before BeginWrite.
                //CreateGraphicFence returns the id of the fence that will be signaled when the GPU finishes reading the buffer.
                //and add the command to the GPU queue. The CPU can then poll the fence to know when it is safe to reuse the buffer.
                _latestRenderFences[_activeRenderSlot] = Graphics.CreateGraphicsFence(
                    GraphicsFenceType.CPUSynchronisation,
                    SynchronisationStageFlags.AllGPUOperations);
                _hasRenderFence[_activeRenderSlot] = true;

                yield return TaskContract.Yield.It;
            }
        }

        //Coordinator root on its own thread. Waits for the main-thread-paced mapped region,
        //lets the Burst tasks fill it, then signals the main thread to close the write.
        IEnumerator<TaskContract> ComputeLoop()
        {
            while (stopping == false)
            {
                //wait for the main thread to map the region and publish it through _frameData
                yield return _regionMapped.Wait().Continue();

                yield return _particleTasks.Run().Continue();

                //workers are done filling the mapped region: main can EndWrite and draw it
                _computeDone.Signal();
            }
        }

        void EndWriteIfOpen()
        {
            if (_writeOpen == false)
                return;

            _openBuffer.EndWrite<float3>((int) _particleCount);
            _writeOpen = false;
            _openBuffer = null;
            _frameData.Get<AdvancedSyncFrameData>(0).uploadRegion = default;
        }

        readonly uint[] _GPUInstancingArgs = {0, 0, 0, 0, 0};

        ComputeBuffer[] _uploadBuffers;
        ComputeBuffer _openBuffer;
        ComputeBuffer _activeRenderBuffer;
        ComputeBuffer _albedoBuffer;
        ComputeBuffer _GPUInstancingArgsBuffer;
        Mesh _pointMesh;
        GraphicsFence[] _latestRenderFences;
        bool[] _hasRenderFence;

        NativeDynamicArray _cpuParticles;
        NativeDynamicArray _frameData;
        MultiThreadedBurstParallelTaskCollection<AdvancedSyncBurstRangeTask> _particleTasks;

        SteppableRunner _updateRunner;
        MultiThreadRunner _multiThreadRunner;
        RegionMappedSignal _regionMapped;
        ComputeDoneSignal _computeDone;
        int _frameIndex;
        int _activeRenderSlot;
        bool _stopping;
        bool _writeOpen;

        bool stopping => Volatile.Read(ref _stopping);
    }

    struct AdvancedSyncFrameData
    {
        public AdvancedSyncFrameData(NativeArray<float3> uploadRegion, float time)
        {
            this.uploadRegion = uploadRegion;
            this.time = time;
        }

        public NativeArray<float3> uploadRegion;
        public float time;
    }

    struct AdvancedSyncBurstRangeTask : IBurstParallelTask
    {
        public AdvancedSyncBurstRangeTask(NativeDynamicArray input, NativeDynamicArray frameData)
        {
            _input      = input;
            _frameData  = frameData;
            _startIndex = 0;
            _count      = 0;
        }

        public void SetRange(int startIndex, int count)
        {
            _startIndex = startIndex;
            _count = count;
        }

        public bool MoveNext()
        {
            ref AdvancedSyncFrameData frameData = ref _frameData.Get<AdvancedSyncFrameData>(0);
            MillionPointsAdvancedSyncBurstKernel.Execute(
                ref _input, ref frameData.uploadRegion, frameData.time, _startIndex, _count);
            return false;
        }

        public void Dispose()
        {
            // The component owns the shared native arrays.
        }

        public void Reset()
        {
        }

        public object Current => null;

        NativeDynamicArray _input;
        NativeDynamicArray _frameData;
        int _startIndex;
        int _count;
    }

    [BurstCompile]
    static class MillionPointsAdvancedSyncBurstKernel
    {
        [BurstCompile(CompileSynchronously = true)]
        public static void Execute(ref NativeDynamicArray input, ref NativeArray<float3> output,
                                   float time, int startIndex, int count)
        {
            int endIndex = startIndex + count;

            for (int index = startIndex; index < endIndex; index++)
            {
                ref BurstCPUParticleData particle = ref input.Get<BurstCPUParticleData>(index);
                float3 randomVector = math.normalize(
                    math.cross(RandomVector((uint) index + 1), particle.basePosition));

                output[index] = RotatePosition(
                    particle.basePosition, randomVector, particle.rotationSpeed * time);
            }
        }

        static uint Hash(uint value)
        {
            value ^= 2747636419u;
            value *= 2654435769u;
            value ^= value >> 16;
            value *= 2654435769u;
            value ^= value >> 16;
            value *= 2654435769u;
            return value;
        }

        static float RandomFloat(uint seed)
        {
            return Hash(seed) / 4294967295.0f;
        }

        static float3 RandomVector(uint seed)
        {
            const float Pi2 = 6.28318530718f;
            float z = 1.0f - 2.0f * RandomFloat(seed);
            float xy = math.sqrt(1.0f - z * z);
            math.sincos(Pi2 * RandomFloat(seed + 1), out float sin, out float cos);
            float3 unitVector = new float3(sin * xy, cos * xy, z);
            return unitVector * math.sqrt(RandomFloat(seed + 2));
        }

        static float3 RotatePosition(float3 position, float3 axis, float angle)
        {
            float halfAngle = angle * 0.5f * 3.14159f / 180.0f;
            math.sincos(halfAngle, out float sin, out float cos);
            float4 quaternion = new float4(axis * sin, cos);

            return position + 2.0f * math.cross(
                quaternion.xyz, math.cross(quaternion.xyz, position) + quaternion.w * position);
        }
    }
}
