using Svelto.DataStructures;
using Svelto.Tasks.Parallelism;
using Svelto.Tasks.Parallelism.ExtraLean;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Svelto.Tasks.Example.MillionPoints.Multithreading
{
    // Stateless direct-write Burst support for IndependentThreads. The main thread
    // publishes a mapped ComputeBuffer region through IndependentThreadsFrameData;
    // range tasks then write their disjoint portions of that region directly.
    // This file deliberately owns no MonoBehaviour lifecycle or synchronization.
    struct IndependentThreadsBurstParticleData
    {
        public IndependentThreadsBurstParticleData(float3 basePosition, float rotationSpeed)
        {
            this.basePosition = basePosition;
            this.rotationSpeed = rotationSpeed;
        }

        public float3 basePosition;
        public float rotationSpeed;
    }

    struct IndependentThreadsFrameData
    {
        public IndependentThreadsFrameData(NativeArray<float3> uploadRegion, float time)
        {
            this.uploadRegion = uploadRegion;
            this.time = time;
        }

        public NativeArray<float3> uploadRegion;
        public float time;
    }

    struct IndependentThreadsBurstRangeTask : IBurstParallelTask
    {
        public IndependentThreadsBurstRangeTask(NativeDynamicArray input, NativeDynamicArray frameData)
        {
            _input = input;
            _frameData = frameData;
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
            ref IndependentThreadsFrameData frameData =
                ref _frameData.Get<IndependentThreadsFrameData>(0);
            MillionPointsIndependentThreadsBurstKernel.Execute(
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
    static class MillionPointsIndependentThreadsBurstKernel
    {
        [BurstCompile(CompileSynchronously = true)]
        public static void Execute(ref NativeDynamicArray input, ref NativeArray<float3> output,
                                   float time, int startIndex, int count)
        {
            int endIndex = startIndex + count;

            for (int index = startIndex; index < endIndex; index++)
            {
                ref IndependentThreadsBurstParticleData particle =
                    ref input.Get<IndependentThreadsBurstParticleData>(index);
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
