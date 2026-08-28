using System;
using System.Collections;
using System.Threading;
using Svelto.Common;
using Svelto.DataStructures;
using Svelto.Tasks;
using Svelto.Tasks.Enumerators;
using Svelto.Tasks.ExtraLean;
using Svelto.Tasks.Parallelism;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Svelto.Tasks.Example.MillionPoints.Multithreading
{
    // Signal-based pipeline. Its dedicated MultiThreadRunner is only a
    // coordinator: every particle pass runs through the Burst range-task
    // collection shared with the other Svelto Burst examples.
    public class MillionPointsCPU_AdvancedSync : MonoBehaviour
    {
        [TextArea] public string Notes =
            "Advanced synchronization strategy (Burst, pipelined): while the main " +
            "thread writes and renders pass N through a mapped GPU upload region, " +
            "a coordinator runs Burst pass N+1 into the other result buffer.";

        [SerializeField] uint _particleCount;
        [SerializeField] Material _material;
        [SerializeField] Shader _shader;
        [SerializeField] Vector3 _BoundCenter = Vector3.zero;
        [SerializeField] Vector3 _BoundSize = new Vector3(300f, 300f, 300f);

        public class MainThreadSignal : WaitForSignal<MainThreadSignal>
        {
            public MainThreadSignal(string name, float timeout = 1000) : base(name, timeout) { }
        }

        public class OtherThreadSignal : WaitForSignal<OtherThreadSignal>
        {
            public OtherThreadSignal(string name, float timeout = 1000) : base(name, timeout) { }
        }

        void Awake()
        {
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
        }

        void OnEnable()
        {
            Volatile.Write(ref _stopping, false);
            _completedSlot = 0;
            _updateRunner = new SteppableRunner("MillionPoints.AdvancedSync.Update");
            _multiThreadRunner = new MultiThreadRunner("MillionPoints.AdvancedSync.Coordinator");

            InitializeParticleData();
            InitializeRendering();
            InitializeTasks();
            PipelinedSignalBasedMultithreading().RunOn(_updateRunner);
        }

        void Update()
        {
            _updateRunner.Step();
        }

        void OnDisable()
        {
            Volatile.Write(ref _stopping, true);
            _mainWait?.Signal();
            _otherWait?.Signal();

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
                    "MillionPoints.AdvancedSync", workerCount, true);
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

        IEnumerator PipelinedSignalBasedMultithreading()
        {
            var bounds = new Bounds(_BoundCenter, _BoundSize);
            _mainWait = new MainThreadSignal("AdvancedSync.Main", 1000);
            _otherWait = new OtherThreadSignal("AdvancedSync.Worker", 1000);
            _frameTime = Time.time;

            OperationsRunningOnOtherThreads().RunOn(_multiThreadRunner);

            while (stopping == false)
            {
                _otherWait.Wait().Complete();

                _frameTime = Time.time;
                _mainWait.Signal();

                NativeArray<float3> uploadRegion =
                    _particleDataBuffer.BeginWrite<float3>(0, (int) _particleCount);
                NativeArray<float3>.Copy(GetOutputBuffer(Volatile.Read(ref _completedSlot)), uploadRegion);
                _particleDataBuffer.EndWrite<float3>((int) _particleCount);
                Graphics.DrawMeshInstancedIndirect(_pointMesh, 0, _material, bounds, _GPUInstancingArgsBuffer);

                yield return null;
            }
        }

        IEnumerator OperationsRunningOnOtherThreads()
        {
            int pass = 0;

            while (stopping == false)
            {
                RunParticleJobs(pass & 1, _frameTime);
                Volatile.Write(ref _completedSlot, pass & 1);
                _otherWait.Signal();

                _mainWait.Wait().Complete();
                pass++;

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
        MainThreadSignal _mainWait;
        OtherThreadSignal _otherWait;
        int _completedSlot;
        float _frameTime;
        bool _stopping;

        bool stopping => Volatile.Read(ref _stopping);
    }
}
