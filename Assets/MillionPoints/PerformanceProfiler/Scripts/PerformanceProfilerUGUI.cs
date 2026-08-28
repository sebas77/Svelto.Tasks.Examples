using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace PerformanceCheker
{
    [RequireComponent(typeof(PerformanceProfiler))]
    public class PerformanceProfilerUGUI : MonoBehaviour
    {
        [SerializeField]
        Text text;

        PerformanceProfiler profiler;

        readonly StringBuilder bufferedString = new StringBuilder(320);

        // Last rendered values. Every character pushed to the Text is derived from
        // these ints, so when they are unchanged the string would be identical:
        // skip rebuilding and assigning entirely and steady-state frames allocate nothing.
        int cachedFpsMs = int.MinValue;
        int cachedMaxMs = int.MinValue;
        int cachedMinMs = int.MinValue;
        int cachedGcCount = -1;
#if BENCHMARK
        int cachedParticlesCount = -1;
#endif

        void Start()
        {
            profiler = GetComponent<PerformanceProfiler>();
            if (text == null)
            {
                Destroy(this);
            }
        }

        void Update()
        {
            int fpsMs   = profiler.CurrentFPS;
            int maxMs   = profiler.MaxFPS;
            int minMs   = profiler.MinFPS;
            int gcCount = profiler.GCcount;
#if BENCHMARK
            int particlesCount = PerformanceProfiler.particlesCount;
#endif

#if BENCHMARK
            if (fpsMs == cachedFpsMs && maxMs == cachedMaxMs && minMs == cachedMinMs &&
                gcCount == cachedGcCount && particlesCount == cachedParticlesCount)
                return;
#else
            if (fpsMs == cachedFpsMs && maxMs == cachedMaxMs && minMs == cachedMinMs &&
                gcCount == cachedGcCount)
                return;
#endif

            cachedFpsMs   = fpsMs;
            cachedMaxMs   = maxMs;
            cachedMinMs   = minMs;
            cachedGcCount = gcCount;
#if BENCHMARK
            cachedParticlesCount = particlesCount;
#endif

            bufferedString.Length = 0;

            bufferedString.Append("ms:");
            bufferedString.Append(fpsMs);
            bufferedString.Append("\r\n");
            bufferedString.Append("Max ms:");
            bufferedString.Append(maxMs);
            bufferedString.Append("\r\n");
            bufferedString.Append("Min ms:");
            bufferedString.Append(minMs);
#if BENCHMARK
            bufferedString.Append("\r\n");
            bufferedString.Append("Particles Transformed:");
            bufferedString.Append(particlesCount);
#endif
            bufferedString.Append("\r\n");
            bufferedString.Append("GC Alloc Num:");
            bufferedString.Append(gcCount);

            // ToString allocates: paid only on real value changes (a few times per
            // second), never per frame. Collections triggered inside this window are
            // excluded from the displayed GC count.
            int collectionsBefore = System.GC.CollectionCount(0);
            text.text = bufferedString.ToString();
            PerformanceProfiler.AttributeCollectionsToGui(
                System.GC.CollectionCount(0) - collectionsBefore);
        }
    }
}
