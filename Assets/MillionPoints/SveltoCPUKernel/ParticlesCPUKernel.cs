using System;
using Svelto.Tasks.Parallelism;
using Unity.Collections;
using UnityEngine;

namespace Svelto.Tasks.Example.MillionPoints.Multithreading
{
    struct ParticlesCPUKernel : ISveltoJob
    {
        CPUParticleData[] _particleData;
        NativeArray<GPUParticleData> _gpuParticlesData;

        static uint Hash(uint s)
        {
            s ^= 2747636419u;
            s *= 2654435769u;
            s ^= s >> 16;
            s *= 2654435769u;
            s ^= s >> 16;
            s *= 2654435769u;
            return s;
        }

        static float Randomf(uint seed)
        {
            return Hash(seed) / 4294967295.0f; // 2^32-1
        }

        static void RandomUnitVector(uint seed, out Vector3 result)
        {
            float PI2 = 6.28318530718f;
            float z = 1.0f - 2.0f * Randomf(seed);
            float xy = (float) Math.Sqrt(1.0 - z * z);
            float sn, cs;
            var value = PI2 * Randomf(seed + 1);
            sn = (float) Math.Sin(value);
            cs = (float) Math.Cos(value);
            result.x = sn * xy;
            result.y = cs * xy;
            result.z = z;
        }

        static void RandomVector(uint seed, out Vector3 result)
        {
            RandomUnitVector(seed, out result);
            var sqrt = (float) Math.Sqrt(Randomf(seed + 2));
            result.x = result.x * sqrt;
            result.y = result.y * sqrt;
            result.z = result.z * sqrt;
        }

        static float quat_from_axis_angle(ref Vector3 axis, float angle, out Vector3 result)
        {
            float half_angle = (angle * 0.5f) * 3.14159f / 180.0f;
            var sin = (float) Math.Sin(half_angle);
            result.x = axis.x * sin;
            result.y = axis.y * sin;
            result.z = axis.z * sin;
            return (float) Math.Cos(half_angle);
        }

        static void Cross(ref Vector3 lhs, ref Vector3 rhs, out Vector3 result)
        {
            result.x = lhs.y * rhs.z - lhs.z * rhs.y;
            result.y = lhs.z * rhs.x - lhs.x * rhs.z;
            result.z = lhs.x * rhs.y - lhs.y * rhs.x;
        }

        static void rotate_position(ref Vector3 position, ref Vector3 axis, float angle, out Vector3 result)
        {
            Vector3 q;
            var w = quat_from_axis_angle(ref axis, angle, out q);
            Cross(ref q, ref position, out result);
            result.x = result.x + w * position.x;
            result.y = result.y + w * position.y;
            result.z = result.z + w * position.z;
            Vector3 otherResult;
            Cross(ref q, ref result, out otherResult);
            result.x = position.x + 2.0f * otherResult.x;
            result.y = position.y + 2.0f * otherResult.y;
            result.z = position.z + 2.0f * otherResult.z;
        }

        public ParticlesCPUKernel(MillionPointsCPU t) : this()
        {
            _particleData = t._cpuParticleDataArr;
            _gpuParticlesData = t._gpuparticleDataArr;
        }

        public void Dispose() { }

        public void Update(int i)
        {
            RandomVector((uint) i + 1, out var randomVector);
            Cross(ref randomVector, ref _particleData[i].basePosition, out randomVector);

            randomVector.Normalize();

            Vector3 position;
            rotate_position(ref _particleData[i].basePosition,
                ref randomVector, _particleData[i].rotationSpeed * MillionPointsCPU._time,
                out position);

            _gpuParticlesData[i] = new GPUParticleData(position, _gpuParticlesData[i].albedo);
        }
    }
}