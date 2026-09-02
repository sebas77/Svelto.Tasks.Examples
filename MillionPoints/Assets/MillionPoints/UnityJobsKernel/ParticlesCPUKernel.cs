using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Svelto.Tasks.Example.MillionPoints.UnityJobs
{
    // Burst-compiled IJobParallelFor kernel. The math is written for optimal
    // Burst codegen:
    //  - all state is float3/float4 Unity.Mathematics: math.cross and the
    //    quaternion rotation vectorize to SIMD instructions
    //  - math.normalize uses rsqrt (one approximate reciprocal sqrt) instead of
    //    the Vector3 sqrt-then-divide pair
    //  - math.sincos evaluates sin AND cos with a single transcendental call;
    //    the old code called Math.Sin and Math.Cos separately
    //  - every intermediate is float32. The old code promoted several operations
    //    to double precision (Math.Sqrt(1.0 - z*z), Math.Sin/Cos), which forces
    //    scalar double-precision library calls in the hot loop and disables SIMD
    //  - _time is a plain field copied in at Schedule() time: Burst cannot read
    //    mutable static fields, and static readonly would be constant-folded at
    //    compile time (freezing the particle rotation)
    //  - _particleDataArr is [ReadOnly] (basePosition/rotationSpeed never
    //    change), which gives the job scheduler maximum freedom; the original
    //    wrote the identical struct back every iteration for no reason
    [BurstCompile]
    struct ParticlesCPUKernel : IJobParallelFor
    {
        [ReadOnly] public NativeArray<CPUParticleData> _particleDataArr;
        // SoA: the job writes only the positions; the albedo lives in a
        // separate static buffer never touched after init.
        [WriteOnly] public NativeArray<float3> _gpuparticleDataArr;
        public float _time;

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

        static float3 RandomUnitVector(uint seed)
        {
            const float PI2 = 6.28318530718f;
            float z = 1.0f - 2.0f * Randomf(seed);
            float xy = math.sqrt(1.0f - z * z);
            math.sincos(PI2 * Randomf(seed + 1), out float sn, out float cs);
            return new float3(sn * xy, cs * xy, z);
        }

        static float3 RandomVector(uint seed)
        {
            //random unit vector scaled by sqrt(random)
            return RandomUnitVector(seed) * math.sqrt(Randomf(seed + 2));
        }

        static float4 quat_from_axis_angle(float3 axis, float angle)
        {
            float half_angle = (angle * 0.5f) * 3.14159f / 180.0f;
            math.sincos(half_angle, out float sin, out float cos);
            //quaternion (xyz = axis * sin(half), w = cos(half))
            return new float4(axis * sin, cos);
        }

        static float3 rotate_position(float3 position, float3 axis, float angle)
        {
            //v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v)
            float4 q = quat_from_axis_angle(axis, angle);
            float3 qxyz = q.xyz;

            return position + 2.0f * math.cross(qxyz, math.cross(qxyz, position) + q.w * position);
        }

        public ParticlesCPUKernel(NativeArray<CPUParticleData> particleData)
        {
            _particleDataArr = particleData;
            _gpuparticleDataArr = default;
            _time = 0;
        }

        public void Execute(int i)
        {
            var particle = _particleDataArr[i];

            float3 randomVector = RandomVector((uint) i + 1);
            randomVector = math.normalize(math.cross(randomVector, particle.basePosition));

            _gpuparticleDataArr[i] = rotate_position(particle.basePosition, randomVector,
                                                     particle.rotationSpeed * _time);
        }
    }
}
