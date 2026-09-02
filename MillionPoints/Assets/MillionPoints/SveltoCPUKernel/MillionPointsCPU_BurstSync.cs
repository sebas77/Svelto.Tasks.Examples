using System;
using System.Collections.Generic;
using Svelto.DataStructures;
using Svelto.Tasks.Lean;
using Svelto.Tasks.Parallelism;
using Svelto.Tasks.Parallelism.ExtraLean;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;

namespace Svelto.Tasks.Example.MillionPoints.Multithreading
{
    // Direct Svelto.Tasks counterpart to MillionPointsCPUUnityJobs. The main
    // thread maps the GPU upload region, Burst range tasks fill it, then the
    // main thread closes the write and renders.
    public class MillionPointsCPU_BurstSync : MonoBehaviour
    {
        [TextArea] public string Notes =
            "Burst Svelto.Tasks baseline: Burst range tasks write directly to " +
            "the mapped GPU upload region, then the main thread renders.";

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
            _stopping = false;
            _hasCompletedRenderBuffer = false;
            AllocateParticleData();
            InitializeParticleData();
            InitializeRendering();
            InitializeTasks();

            //Slot 0 is reserved as the initial render slot; the first upload uses slot 1.
            _frameIndex = 1;
            _updateRunner = new SteppableRunner("MillionPoints.BurstSync.Update");
            UploadParticles().RunOn(_updateRunner);
            RenderParticles().RunOn(_updateRunner);
        }

        void Update()
        {
            _updateRunner.Step();
        }

        //Double-buffered ownership for slot = _frameIndex & 1:
        //
        //  latestFence[slot].passed --> BeginWrite --> Burst tasks --> EndWrite --> publish slot
        //             ^                                                               |
        //             |                                                               v
        //        CreateGraphicsFence <--------------------------- RenderLoop draws that slot
        //
        //Each slot retains only its newest fence. Passing it proves all earlier graphics-queue
        //draws using that slot have completed, so the CPU may map it again. If the selected
        //slot is still GPU-owned, this loop yields while RenderParticles keeps drawing the
        //other completed slot.
        IEnumerator<TaskContract> UploadParticles()
        {
            while (_stopping == false)
            {
                int slot = _frameIndex++ & 1;
                ComputeBuffer buffer = _uploadBuffers[slot];

                while (_hasRenderFence[slot] && _latestRenderFences[slot].passed == false)
                    yield return TaskContract.Yield.It;

                _hasRenderFence[slot] = false;

                NativeArray<float3> uploadRegion =
                    buffer.BeginWrite<float3>(0, (int) _particleCount);
                _openBuffer = buffer;
                _writeOpen = true;

                _frameData.Set(0, new BurstSyncFrameData(uploadRegion, Time.time));

                yield return _particleTasks.Run().Continue();

                EndWriteIfOpen();
                _activeBuffer = buffer;
                _activeRenderSlot = slot;
                _hasCompletedRenderBuffer = true;

                yield return TaskContract.Yield.It;
            }
        }

        IEnumerator<TaskContract> RenderParticles()
        {
            while (_stopping == false)
            {
                if (_hasCompletedRenderBuffer)
                {
                    _material.SetBuffer("_ParticleDataBuffer", _activeBuffer);
                    Graphics.DrawMeshInstancedIndirect(
                        _pointMesh, 0, _material, _bounds, _GPUInstancingArgsBuffer);

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
        void OnDisable()
        {
            _stopping = true;

            if (_particleTasks != null)
            {
                if (_particleTasks.isRunning)
                    _particleTasks.Complete();

                EndWriteIfOpen();

                _updateRunner?.Dispose();
                _updateRunner = null;

                _particleTasks.Dispose();
                _particleTasks = null;
            }

            if (_frameData.isValid)
                _frameData.Dispose();

            _latestRenderFences = null;
            _hasRenderFence = null;

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

        void EndWriteIfOpen()
        {
            if (_writeOpen == false)
                return;

            _openBuffer.EndWrite<float3>((int) _particleCount);
            _writeOpen = false;
            _openBuffer = null;
            _frameData.Get<BurstSyncFrameData>(0).uploadRegion = default;
        }

        void AllocateParticleData()
        {
            _cpuParticles = NativeDynamicArray.Alloc<BurstCPUParticleData>(
                Svelto.Common.Allocator.Persistent, _particleCount);
            _frameData = NativeDynamicArray.Alloc<BurstSyncFrameData>(
                Svelto.Common.Allocator.Persistent, 1);
        }

        void InitializeParticleData()
        {
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

            _material.shader = _shader;
            _material.SetBuffer("_ParticleDataBuffer", _uploadBuffers[0]);
            _material.SetBuffer("_AlbedoBuffer", _albedoBuffer);
            _bounds = new Bounds(_BoundCenter, _BoundSize);
            _activeBuffer = _uploadBuffers[0];
            _activeRenderSlot = 0;
        }

        void InitializeTasks()
        {
            uint workerCount = (uint) Math.Max(1, Environment.ProcessorCount - 1);

            //forces the Burst compile at init: zero iterations, default output region
            NativeArray<float3> emptyRegion = default;
            MillionPointsBurstKernel.Execute(ref _cpuParticles, ref emptyRegion, 0f, 0, 0);

            _particleTasks =
                new MultiThreadedBurstParallelTaskCollection<MillionPointsBurstRangeTask>(
                    "MillionPoints.Burst", workerCount, true);
            //the collection itself splits the range in 8192-particle tasks: idle runners
            //steal them through the collection's idle callbacks, self-balancing the load
            _particleTasks.Add(
                new MillionPointsBurstRangeTask(_cpuParticles, _frameData),
                (int) _particleCount, _elementsPerTask);
        }

        //DrawMeshInstancedIndirect reads a fixed 5-uint argument block from the
        //IndirectArguments buffer: {index count per instance, instance count, start index,
        //base vertex, start instance}. Only the first two are meaningful here (1 point per
        //instance, _particleCount instances); the rest stay 0 but must exist because the GPU
        //always consumes the full 20-byte block.
        readonly uint[] _GPUInstancingArgs = {0, 0, 0, 0, 0};

        //Double-buffered particle positions: the CPU writes one slot through BeginWrite/EndWrite
        //while the GPU renders the other. Indexed by _frameIndex & 1.
        ComputeBuffer[] _uploadBuffers;
        //Slot currently mapped for CPU writing (BeginWrite opened, EndWrite still pending)
        ComputeBuffer _openBuffer;
        //Last fully written slot, currently bound to the material for rendering
        ComputeBuffer _activeBuffer;
        //Static per-particle colors, uploaded once at startup
        ComputeBuffer _albedoBuffer;
        //Indirect-arguments buffer for DrawMeshInstancedIndirect (index count, instance count, ...)
        ComputeBuffer _GPUInstancingArgsBuffer;
        Mesh _pointMesh;
        Bounds _bounds;
        //Latest GraphicsFence issued after drawing each slot; .passed means the GPU has
        //finished reading that slot and the CPU may map it for writing again
        GraphicsFence[] _latestRenderFences;
        //True when a fence is pending for that slot (the draw may still be in flight on the GPU);
        //cleared once the fence passes and the slot is safe to reuse
        bool[] _hasRenderFence;

        NativeDynamicArray _cpuParticles;
        NativeDynamicArray _frameData;
        MultiThreadedBurstParallelTaskCollection<MillionPointsBurstRangeTask> _particleTasks;
        SteppableRunner _updateRunner;
        int _frameIndex;
        int _activeRenderSlot;
        bool _stopping;
        bool _writeOpen;
        bool _hasCompletedRenderBuffer;
    }

    struct BurstSyncFrameData
    {
        public BurstSyncFrameData(NativeArray<float3> uploadRegion, float time)
        {
            this.uploadRegion = uploadRegion;
            this.time = time;
        }

        public NativeArray<float3> uploadRegion;
        public float time;
    }

    struct MillionPointsBurstRangeTask : IBurstParallelTask
    {
        public MillionPointsBurstRangeTask(NativeDynamicArray input, NativeDynamicArray frameData)
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
            ref BurstSyncFrameData frameData = ref _frameData.Get<BurstSyncFrameData>(0);
            MillionPointsBurstKernel.Execute(
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
    static class MillionPointsBurstKernel
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

    struct BurstCPUParticleData
    {
        public BurstCPUParticleData(float3 basePosition, float rotationSpeed)
        {
            this.basePosition = basePosition;
            this.rotationSpeed = rotationSpeed;
        }

        public float3 basePosition;
        public float rotationSpeed;
    }

}
