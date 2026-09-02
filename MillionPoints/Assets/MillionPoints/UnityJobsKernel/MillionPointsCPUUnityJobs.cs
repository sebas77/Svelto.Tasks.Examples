using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
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
        
        ComputeBuffer[] _uploadBuffers;
        ComputeBuffer _activeBuffer;
        int _frameIndex;
        int _activeRenderSlot;
        GraphicsFence[] _latestRenderFences;
        bool[] _hasRenderFence;
        bool _hasCompletedRenderBuffer;
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
            _uploadBuffers = new ComputeBuffer[2];
            for (int i = 0; i < _uploadBuffers.Length; i++)
                _uploadBuffers[i] = new ComputeBuffer(_particleCount, sizeof(float) * 3,
                    ComputeBufferType.Structured, ComputeBufferMode.SubUpdates);
            _latestRenderFences = new GraphicsFence[_uploadBuffers.Length];
            _hasRenderFence = new bool[_uploadBuffers.Length];
            _hasCompletedRenderBuffer = false;
            _frameIndex = 0;

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
            _pointMesh.SetIndices(new[] {0}, MeshTopology.Points, 0);

            _GPUInstancingArgsBuffer = new ComputeBuffer(1,
                _GPUInstancingArgs.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
            _GPUInstancingArgs[0] = (_pointMesh != null) ? _pointMesh.GetIndexCount(0) : 0;
            _GPUInstancingArgs[1] = (uint) _particleCount;
            _GPUInstancingArgsBuffer.SetData(_GPUInstancingArgs);

            _material.shader = _shader;
            _material.SetBuffer("_ParticleDataBuffer", _uploadBuffers[0]);
            _material.SetBuffer("_AlbedoBuffer", _albedoBuffer);

            _bounds = new Bounds(_BoundCenter, _BoundSize);
            _job = new ParticlesCPUKernel(_cpuParticleDataArr);
            _activeBuffer = _uploadBuffers[0];
            _activeRenderSlot = 0;
        }

        void Update()
        {
            //Two-slot ownership for slot = _frameIndex & 1:
            //
            //  latestFence[slot].passed --> BeginWrite --> JobHandle.Complete --> EndWrite
            //             ^                                                        |
            //             |                                                        v
            //        CreateGraphicsFence <----------------------- Draw <--- publish slot
            //
            //The latest fence is retained per slot. Passing it proves every earlier draw
            //using that slot has completed. Update cannot yield, so if the next slot's fence
            //is pending this frame skips only the upload and continues rendering the most
            //recent completed slot. No third buffer is allocated.
            int slot = _frameIndex & 1;

            if (_hasRenderFence[slot] == false || _latestRenderFences[slot].passed)
            {
                ComputeBuffer uploadBuffer = _uploadBuffers[slot];
                _hasRenderFence[slot] = false;

                NativeArray<float3> uploadRegion =
                    uploadBuffer.BeginWrite<float3>(0, _particleCount);

                //Burst cannot read mutable static fields, so the time and mapped
                //output region are copied into the job before Schedule() snapshots it.
                _job._time = UnityEngine.Time.time;
                _job._gpuparticleDataArr = uploadRegion;

                var jobSchedule = _job.Schedule(_particleCount, 32);

                jobSchedule.Complete();
                uploadBuffer.EndWrite<float3>(_particleCount);

                _activeBuffer = uploadBuffer;
                _activeRenderSlot = slot;
                _hasCompletedRenderBuffer = true;
                _frameIndex++;
            }

            if (_hasCompletedRenderBuffer == false)
                return;

            //do something seriously slow
#if DO_SOMETHING_SERIOUSLY_SLOW
            Thread.Sleep(10);
#endif

            _material.SetBuffer("_ParticleDataBuffer", _activeBuffer);
            Graphics.DrawMeshInstancedIndirect(_pointMesh, 0, _material,
                                               _bounds, _GPUInstancingArgsBuffer);
            _latestRenderFences[_activeRenderSlot] = Graphics.CreateGraphicsFence(
                GraphicsFenceType.CPUSynchronisation,
                SynchronisationStageFlags.AllGPUOperations);
            _hasRenderFence[_activeRenderSlot] = true;
        }

        void OnDisable()
        {
            if (_cpuParticleDataArr.IsCreated)
                _cpuParticleDataArr.Dispose();

            _latestRenderFences = null;
            _hasRenderFence = null;

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
