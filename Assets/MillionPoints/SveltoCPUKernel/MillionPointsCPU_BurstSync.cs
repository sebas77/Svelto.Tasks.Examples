using System;
using Svelto.DataStructures;
using Svelto.Tasks.Parallelism;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
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

        void Awake()
        {
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
        }

        void OnEnable()
        {
            AllocateParticleData();
            InitializeParticleData();
            InitializeRendering();
            InitializeTasks();
        }

        void Update()
        {
            _particleTime.Set(0, Time.time);

            NativeArray<float3> uploadRegion =
                _particleDataBuffer.BeginWrite<float3>(0, (int) _particleCount);
            MillionPointsBurstKernel.UploadRegion.Data = uploadRegion;
            _particleTasks.Complete();
            _particleDataBuffer.EndWrite<float3>((int) _particleCount);
            Graphics.DrawMeshInstancedIndirect(_pointMesh, 0, _material, _bounds, _GPUInstancingArgsBuffer);
        }

        void OnDisable()
        {
            if (_particleTasks != null)
            {
                if (_particleTasks.isRunning)
                    _particleTasks.Complete();

                _particleTasks.Dispose();
                _particleTasks = null;
            }

            if (_particleTime.isValid)
                _particleTime.Dispose();

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

        void AllocateParticleData()
        {
            _cpuParticles = NativeDynamicArray.Alloc<BurstCPUParticleData>(
                Svelto.Common.Allocator.Persistent, _particleCount);
            _particleTime = NativeDynamicArray.Alloc<float>(Svelto.Common.Allocator.Persistent, 1);
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

            _particleTime.Set(0, 0.0f);

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
            _bounds = new Bounds(_BoundCenter, _BoundSize);
        }

        void InitializeTasks()
        {
            uint workerCount = (uint) Math.Max(1, Environment.ProcessorCount - 1);

            MillionPointsBurstKernel.Execute(ref _cpuParticles, ref _particleTime, 0, 0);

            _particleTasks =
                new MultiThreadedBurstParallelTaskCollection<MillionPointsBurstRangeTask>(
                    "MillionPoints.Burst", workerCount, true);
            var prototype = new MillionPointsBurstRangeTask(_cpuParticles, _particleTime);
            _particleTasks.Add(in prototype, (int) _particleCount);
        }

        readonly uint[] _GPUInstancingArgs = {0, 0, 0, 0, 0};

        ComputeBuffer _particleDataBuffer;
        ComputeBuffer _albedoBuffer;
        ComputeBuffer _GPUInstancingArgsBuffer;
        Mesh _pointMesh;
        Bounds _bounds;

        NativeDynamicArray _cpuParticles;
        NativeDynamicArray _particleTime;
        MultiThreadedBurstParallelTaskCollection<MillionPointsBurstRangeTask> _particleTasks;
    }

    struct MillionPointsBurstRangeTask : IBurstParallelTask
    {
        public MillionPointsBurstRangeTask(NativeDynamicArray input, NativeDynamicArray time)
        {
            _input = input;
            _time = time;
            _startIndex = 0;
            _count = 0;
        }

        public void SetRange(int startIndex, int count)
        {
            _startIndex = startIndex;
            _count = count;
        }

        public bool MoveNext()
        {
            MillionPointsBurstKernel.Execute(ref _input, ref _time, _startIndex, _count);
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
        NativeDynamicArray _time;
        int _startIndex;
        int _count;
    }

    [BurstCompile]
    static class MillionPointsBurstKernel
    {
        public static readonly SharedStatic<NativeArray<float3>> UploadRegion =
            SharedStatic<NativeArray<float3>>.GetOrCreate<BurstUploadRegionKey>();

        struct BurstUploadRegionKey { }

        [BurstCompile(CompileSynchronously = true)]
        public static void Execute(ref NativeDynamicArray input, ref NativeDynamicArray timeArray,
                                   int startIndex, int count)
        {
            float time = timeArray.Get<float>(0);
            NativeArray<float3> output = UploadRegion.Data;
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
