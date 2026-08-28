using System;
using System.Collections;
using System.Threading;
using Svelto.Common;
using Svelto.DataStructures;
using Svelto.Tasks;
using Svelto.Tasks.ExtraLean;
using Svelto.Tasks.Parallelism;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Svelto.Tasks.Example.MillionPoints.Multithreading
{
    // Latest-wins pipeline. Its dedicated MultiThreadRunner is only a
    // coordinator: particle passes run on the shared-style Burst range-task
    // collection, while the main thread uploads the newest completed result.
    public class MillionPointsCPU_IndependentThreads : MonoBehaviour
    {
        [TextArea] public string Notes =
            "Independent threads strategy (Burst): a coordinator computes back-to-back " +
            "passes into a double buffer and publishes the newest generation. The main " +
            "thread writes and renders the latest completed result through a mapped GPU " +
            "upload region without waiting.";

        [SerializeField] uint _particleCount;
        [SerializeField] Material _material;
        [SerializeField] Shader _shader;
        [SerializeField] Vector3 _BoundCenter = Vector3.zero;
        [SerializeField] Vector3 _BoundSize = new Vector3(300f, 300f, 300f);

        void Awake()
        {
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
        }

        void OnEnable()
        {
            Volatile.Write(ref _stopping, false);
            _publishedGen = -1;
            _ackedGen = -1;
            _updateRunner = new SteppableRunner("MillionPoints.IndependentThreads.Update");
            _multiThreadRunner = new MultiThreadRunner("MillionPoints.IndependentThreads.Coordinator");

            InitializeParticleData();
            InitializeRendering();
            InitializeTasks();
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

                _particleTasks.Dispose();
                _particleTasks = null;
            }

            _updateRunner?.Dispose();
            _updateRunner = null;
            _multiThreadRunner = null;

            if (_gpuPositionsView.IsCreated)
                _gpuPositionsView.Dispose();

            if (_nativeWriteSlot.isValid)
                _nativeWriteSlot.Dispose();

            if (_particleTime.isValid)
                _particleTime.Dispose();

            if (_gpuPositions.isValid)
                _gpuPositions.Dispose();

            if (_cpuParticles.isValid)
                _cpuParticles.Dispose();

            if (_particleDataBuffer != null)
            {
                _particleDataBuffer.Release();
                _particleDataBuffer = null;
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
            _cpuParticles = NativeDynamicArray.Alloc<PipelinedBurstParticleData>(
                Svelto.Common.Allocator.Persistent, _particleCount);
            _gpuPositions = NativeDynamicArray.Alloc<float3>(
                Svelto.Common.Allocator.Persistent, _particleCount * 2);
            _particleTime = NativeDynamicArray.Alloc<float>(Svelto.Common.Allocator.Persistent, 1);
            _nativeWriteSlot = NativeDynamicArray.Alloc<int>(Svelto.Common.Allocator.Persistent, 1);

            var albedos = new float3[(int) _particleCount];
            for (uint index = 0; index < _particleCount; index++)
            {
                _cpuParticles.Set(index, new PipelinedBurstParticleData(
                    new float3(Random.Range(-10.0f, 10.0f), Random.Range(-10.0f, 10.0f),
                               Random.Range(-10.0f, 10.0f)), Random.Range(1.0f, 100.0f)));
                albedos[(int) index] = new float3(
                    Random.Range(0.0f, 1.0f), Random.Range(0.0f, 1.0f), Random.Range(0.0f, 1.0f));
            }

            _particleTime.Set(0, 0.0f);
            _nativeWriteSlot.Set(0, 0);
            _gpuPositions.SetCount<float3>(_particleCount * 2);
            _gpuPositionsView = _gpuPositions.ToNativeArray<float3>();

            _albedoBuffer = new ComputeBuffer((int) _particleCount, sizeof(float) * 3);
            _albedoBuffer.SetData(albedos);
        }

        void InitializeRendering()
        {
            _particleDataBuffer = new ComputeBuffer((int) _particleCount, sizeof(float) * 3,
                ComputeBufferType.Structured, ComputeBufferMode.SubUpdates);

            _pointMesh = new Mesh
            {
                vertices = new[] {new Vector3(0, 0)},
                normals = new[] {new Vector3(0, 1, 0)}
            };
            _pointMesh.SetIndices(new[] {0}, MeshTopology.Points, 0);

            _GPUInstancingArgsBuffer = new ComputeBuffer(
                1, _GPUInstancingArgs.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
            _GPUInstancingArgs[0] = _pointMesh.GetIndexCount(0);
            _GPUInstancingArgs[1] = _particleCount;
            _GPUInstancingArgsBuffer.SetData(_GPUInstancingArgs);

            _material.shader = _shader;
            _material.SetBuffer("_ParticleDataBuffer", _particleDataBuffer);
            _material.SetBuffer("_AlbedoBuffer", _albedoBuffer);
        }

        void InitializeTasks()
        {
            uint workerCount = (uint) Math.Max(1, Environment.ProcessorCount - 1);

            MillionPointsPipelinedBurstKernel.Execute(
                ref _cpuParticles, ref _gpuPositions, ref _particleTime, ref _nativeWriteSlot,
                (int) _particleCount, 0, 0);

            _particleTasks =
                new MultiThreadedBurstParallelTaskCollection<PipelinedBurstRangeTask>(
                    "MillionPoints.IndependentThreads", workerCount, true);
            var prototype = new PipelinedBurstRangeTask(
                _cpuParticles, _gpuPositions, _particleTime, _nativeWriteSlot, (int) _particleCount);
            _particleTasks.Add(in prototype, (int) _particleCount);
        }

        void RunParticleJobs(int writeSlot, float time)
        {
            _particleTime.Set(0, time);
            _nativeWriteSlot.Set(0, writeSlot);
            _particleTasks.Complete();
        }

        NativeArray<float3> GetOutputBuffer(int slot)
        {
            return _gpuPositionsView.GetSubArray(slot * (int) _particleCount, (int) _particleCount);
        }

        IEnumerator WorkerLoop()
        {
            var then = DateTime.Now;
            RenderAndUploadOnMainThread().RunOn(_updateRunner);

            int pass = 0;
            while (stopping == false)
            {
                if (pass >= 2)
                {
                    var spin = new SpinWait();
                    while (Volatile.Read(ref _ackedGen) < pass - 2 && stopping == false)
                        spin.SpinOnce();

                    if (stopping)
                        break;
                }

                float time = (float) (DateTime.Now - then).TotalSeconds;
                RunParticleJobs(pass & 1, time);
                Volatile.Write(ref _publishedGen, pass);
                pass++;

                yield return null;
            }
        }

        IEnumerator RenderAndUploadOnMainThread()
        {
            var bounds = new Bounds(_BoundCenter, _BoundSize);
            int uploadedGen = -1;

            while (stopping == false)
            {
                int generation = Volatile.Read(ref _publishedGen);

                if (generation != uploadedGen)
                {
                    NativeArray<float3> uploadRegion =
                        _particleDataBuffer.BeginWrite<float3>(0, (int) _particleCount);
                    NativeArray<float3>.Copy(GetOutputBuffer(generation & 1), uploadRegion);
                    _particleDataBuffer.EndWrite<float3>((int) _particleCount);
                    uploadedGen = generation;
                }

                Volatile.Write(ref _ackedGen, generation);
                Graphics.DrawMeshInstancedIndirect(_pointMesh, 0, _material, bounds, _GPUInstancingArgsBuffer);

                yield return null;
            }
        }

        readonly uint[] _GPUInstancingArgs = {0, 0, 0, 0, 0};

        ComputeBuffer _particleDataBuffer;
        ComputeBuffer _albedoBuffer;
        ComputeBuffer _GPUInstancingArgsBuffer;
        Mesh _pointMesh;

        NativeDynamicArray _cpuParticles;
        NativeDynamicArray _gpuPositions;
        NativeDynamicArray _particleTime;
        NativeDynamicArray _nativeWriteSlot;
        NativeArray<float3> _gpuPositionsView;
        MultiThreadedBurstParallelTaskCollection<PipelinedBurstRangeTask> _particleTasks;

        SteppableRunner _updateRunner;
        MultiThreadRunner _multiThreadRunner;
        int _publishedGen = -1;
        int _ackedGen = -1;
        bool _stopping;

        bool stopping => Volatile.Read(ref _stopping);
    }
}
