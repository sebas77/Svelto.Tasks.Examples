using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Svelto.Tasks.Example.MillionPoints.UnityJobs
{
    public class MillionPointsCPUUnityJobs : MonoBehaviour
    {
        [TextArea] public string Notes =
            "This is the Unity Jobs version that I maintain for comparison";

        [SerializeField] int _particleCount;
        [SerializeField] Material _material;
        [SerializeField] Shader _shader;
        [SerializeField] Vector3 _BoundCenter = Vector3.zero;
        [SerializeField] Vector3 _BoundSize = new Vector3(300f, 300f, 300f);
        
        ComputeBuffer _particleDataBuffer;
        // SoA: static albedo uploaded once at init, never touched again.
        ComputeBuffer _albedoBuffer;

        readonly uint[] _GPUInstancingArgs = {0, 0, 0, 0, 0};

        ComputeBuffer _GPUInstancingArgsBuffer;
        
        Mesh _pointMesh;

        [NonSerialized]
        public NativeArray<CPUParticleData> _cpuParticleDataArr;

        void Awake()
        {
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
        }

        void OnEnable()
        {
            _cpuParticleDataArr = new NativeArray<CPUParticleData>(_particleCount, Allocator.Persistent);

            // SoA: positions buffer has a 12 bytes stride (albedo used to be
            // reuploaded with it every frame even if it never changed).
            _particleDataBuffer = new ComputeBuffer(_particleCount, sizeof(float) * 3,
                ComputeBufferType.Structured, ComputeBufferMode.SubUpdates);

            // set default position. The structs use Unity.Mathematics float3:
            // identical 3-float memory layout to Vector3, so the ComputeBuffer
            // stride/GPU layout is unchanged.
            for (int i = 0; i < _particleCount; i++)
            {
                _cpuParticleDataArr[i] = new CPUParticleData(
                    new float3(Random.Range(-10.0f, 10.0f),
                               Random.Range(-10.0f, 10.0f), Random.Range(-10.0f, 10.0f)),
                    Random.Range(1.0f, 100.0f));
            }

            // the albedo never changes, so it is generated once and pushed to
            // the GPU once
            var albedos = new float3[_particleCount];
            for (int i = 0; i < _particleCount; i++)
            {
                albedos[i] = new float3(Random.Range(0.0f, 1.0f),
                                         Random.Range(0.0f, 1.0f), Random.Range(0.0f, 1.0f));
            }

            _albedoBuffer = new ComputeBuffer(_particleCount, sizeof(float) * 3);
            _albedoBuffer.SetData(albedos);

            // create point mesh
            _pointMesh = new Mesh();
            _pointMesh.vertices = new[] { new Vector3(0, 0), };
            _pointMesh.normals = new[] { new Vector3(0, 1, 0), };
            _pointMesh.SetIndices(new[] {0}, MeshTopology.Points, 0);

            _GPUInstancingArgsBuffer = new ComputeBuffer(1,
                _GPUInstancingArgs.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
            _GPUInstancingArgs[0] = (_pointMesh != null) ? _pointMesh.GetIndexCount(0) : 0;
            _GPUInstancingArgs[1] = (uint) _particleCount;
            _GPUInstancingArgsBuffer.SetData(_GPUInstancingArgs);

            _material.shader = _shader;
            _material.SetBuffer("_ParticleDataBuffer", _particleDataBuffer);
            _material.SetBuffer("_AlbedoBuffer", _albedoBuffer);

            _bounds = new Bounds(_BoundCenter, _BoundSize);
            _job = new ParticlesCPUKernel(_cpuParticleDataArr);
        }

        void Update()
        {
            NativeArray<float3> uploadRegion =
                _particleDataBuffer.BeginWrite<float3>(0, _particleCount);

            //Burst cannot read mutable static fields, so the time and mapped
            //output region are copied into the job before Schedule() snapshots it.
            _job._time = UnityEngine.Time.time;
            _job._gpuparticleDataArr = uploadRegion;

            var jobSchedule = _job.Schedule(_particleCount, 32);

            jobSchedule.Complete();
            _particleDataBuffer.EndWrite<float3>(_particleCount);

            //do something seriously slow
#if DO_SOMETHING_SERIOUSLY_SLOW
            Thread.Sleep(10);
#endif

            Graphics.DrawMeshInstancedIndirect(_pointMesh, 0, _material,
                                               _bounds, _GPUInstancingArgsBuffer);
        }

        void OnDisable()
        {
            if (_cpuParticleDataArr.IsCreated)
                _cpuParticleDataArr.Dispose();

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

        Bounds _bounds;
        ParticlesCPUKernel _job;
    }

    public struct CPUParticleData
    {
        public float3 basePosition;
        public readonly float rotationSpeed;

        public CPUParticleData(float3 vector3, float range)
        {
            basePosition = vector3;
            rotationSpeed = range;
        }
    }
}
