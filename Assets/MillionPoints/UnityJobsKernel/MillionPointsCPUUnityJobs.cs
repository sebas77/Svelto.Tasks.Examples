using System;
using System.Threading;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Jobs;
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
        [SerializeField] Vector3 _BoundCenter = Vector3.zero;
        [SerializeField] Vector3 _BoundSize = new Vector3(300f, 300f, 300f);
        
        ComputeBuffer _particleDataBuffer;

        readonly uint[] _GPUInstancingArgs = {0, 0, 0, 0, 0};

        ComputeBuffer _GPUInstancingArgsBuffer;
        

        Mesh _pointMesh;

        [NonSerialized]
        public NativeArray<CPUParticleData> _cpuParticleDataArr;
        [NonSerialized]
        public NativeArray<GPUParticleData> _gpuparticleDataArr;

        void Awake()
        {
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
        }

        void Start()
        {
            _cpuParticleDataArr = new NativeArray<CPUParticleData>((int) _particleCount, Allocator.Persistent);
            _gpuparticleDataArr = new NativeArray<GPUParticleData>((int) _particleCount, Allocator.Persistent);

            _particleDataBuffer = new ComputeBuffer((int) _particleCount, Marshal.SizeOf(typeof(GPUParticleData)));

            // set default position
            for (int i = 0; i < _particleCount; i++)
            {
                _cpuParticleDataArr[i] = new CPUParticleData(new Vector3(Random.Range(-10.0f, 10.0f),
                    Random.Range(-10.0f, 10.0f), Random.Range(-10.0f, 10.0f)), Random.Range(1.0f, 100.0f));
            }

            for (int i = 0; i < _particleCount; i++)
            {
                _gpuparticleDataArr[i] = new GPUParticleData(new Vector3(Random.Range(0.0f, 1.0f),
                    Random.Range(0.0f, 1.0f), Random.Range(0.0f, 1.0f)));
            }

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

            var materialShader = Shader.Find("Custom/MillionPointsCPU");
            _material.shader = materialShader;
            _material.SetBuffer("_ParticleDataBuffer", _particleDataBuffer);

            _bounds = new Bounds(_BoundCenter, _BoundSize);
            _job = new ParticlesCPUKernel(this);
        }

        void Update()
        {
            Time = UnityEngine.Time.time / 10;

            var jobSchedule = _job.Schedule(_particleCount, 32);

            jobSchedule.Complete();

            _particleDataBuffer.SetData(_gpuparticleDataArr);

            //do something seriously slow
#if DO_SOMETHING_SERIOUSLY_SLOW
            Thread.Sleep(10);
#endif

            Graphics.DrawMeshInstancedIndirect(_pointMesh, 0, _material,
                                               _bounds, _GPUInstancingArgsBuffer);
        }

        void OnDisable()
        {
            _cpuParticleDataArr.Dispose();
            _gpuparticleDataArr.Dispose();

            if (_particleDataBuffer != null)
            {
                _particleDataBuffer.Release();
                _particleDataBuffer = null;
            }

            if (_GPUInstancingArgsBuffer != null)
            {
                _GPUInstancingArgsBuffer.Release();
                _GPUInstancingArgsBuffer = null;
            }
        }

        internal static float  Time;
        Bounds _bounds;
        ParticlesCPUKernel _job;
    }

    public struct CPUParticleData
    {
        public Vector3 basePosition;
        public readonly float rotationSpeed;

        public CPUParticleData(Vector3 vector3, float range)
        {
            basePosition = vector3;
            rotationSpeed = range;
        }
    }

    public struct GPUParticleData
    {
        public Vector3 position;
        public Vector3 albedo;

        public GPUParticleData(Vector3 albedo): this()
        {
            this.albedo = albedo;
        }

        public GPUParticleData(Vector3 position, Vector3 albedo)
        {
            this.position = position;
            this.albedo = albedo;
        }
    }
}