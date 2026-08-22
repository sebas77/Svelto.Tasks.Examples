using Svelto.DataStructures;
using Svelto.Tasks.Parallelism;
using Unity.Burst;
using Unity.Mathematics;

namespace Svelto.Tasks.Example.MillionPoints.Multithreading
{
    // Stateless Burst support shared only by AdvancedSync and
    // IndependentThreads. Each component owns its data, output buffers and
    // scheduler; this file deliberately owns no MonoBehaviour lifecycle.
    struct PipelinedBurstParticleData
    {
        public PipelinedBurstParticleData(float3 basePosition, float rotationSpeed)
        {
            this.basePosition = basePosition;
            this.rotationSpeed = rotationSpeed;
        }

        public float3 basePosition;
        public float rotationSpeed;
    }

    struct PipelinedBurstRangeTask : IBurstParallelTask
    {
        public PipelinedBurstRangeTask(NativeDynamicArray input, NativeDynamicArray output,
                                       NativeDynamicArray time, NativeDynamicArray writeSlot,
                                       int particlesPerBuffer)
        {
            _input = input;
            _output = output;
            _time = time;
            _writeSlot = writeSlot;
            _particlesPerBuffer = particlesPerBuffer;
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
            MillionPointsPipelinedBurstKernel.Execute(
                ref _input, ref _output, ref _time, ref _writeSlot, _particlesPerBuffer,
                _startIndex, _count);
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
        NativeDynamicArray _output;
        NativeDynamicArray _time;
        NativeDynamicArray _writeSlot;
        int _particlesPerBuffer;
        int _startIndex;
        int _count;
    }

    [BurstCompile]
    static class MillionPointsPipelinedBurstKernel
    {
        [BurstCompile(CompileSynchronously = true)]
        public static void Execute(ref NativeDynamicArray input, ref NativeDynamicArray output,
                                   ref NativeDynamicArray timeArray, ref NativeDynamicArray writeSlotArray,
                                   int particlesPerBuffer, int startIndex, int count)
        {
            float time = timeArray.Get<float>(0);
            int outputOffset = writeSlotArray.Get<int>(0) * particlesPerBuffer;
            int endIndex = startIndex + count;

            for (int index = startIndex; index < endIndex; index++)
            {
                ref PipelinedBurstParticleData particle =
                    ref input.Get<PipelinedBurstParticleData>(index);
                float3 randomVector = math.normalize(
                    math.cross(RandomVector((uint) index + 1), particle.basePosition));

                ref float3 position = ref output.Get<float3>(outputOffset + index);
                position = RotatePosition(
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
