using System.Collections;
using System;
using UnityEngine;

namespace PerformanceCheker
{
    // Samples frame time and GC collections on a fixed interval. It never builds
    // strings: consumers read the values and decide when the display needs a refresh.
    public class PerformanceProfiler : MonoBehaviour
    {
        // Collections observed inside GUI sections are excluded from GCcount, so the
        // counter only reflects garbage produced by the code under test. A collection
        // fires when gen0 fills up, not at the guilty allocation site, so this is a
        // window-based approximation: GUI allocations are tiny enough that they will
        // essentially never trigger a collection outside these measured windows.
        public int GCcount
        {
            get { return gc_count > s_guiCollections ? gc_count - s_guiCollections : 0; }
        }

        // Called by GUI code around its allocating sections with the CollectionCount(0)
        // delta measured across them.
        public static void AttributeCollectionsToGui(int collectionDelta)
        {
            if (collectionDelta > 0)
                s_guiCollections += collectionDelta;
        }

        static int s_guiCollections;

        public int CurrentFPS
        {
            get { return (int) showingFPSValue; }
        }

        public int MaxFPS
        {
            get { return (int) showingMaxFPSValue; }
        }

        public int MinFPS
        {
            get { return (int) showingMinFPSValue; }
        }

        int gc_start_count_=0;
        int gc_count=0;

        //FPS check
        float FPSCheckIntervalSecond = 0.3f;
        int frameCount = 0;
        public static float showingFPSValue=0f;
        float showingMaxFPSValue=0f;
        float showingMinFPSValue=float.MaxValue;
        float timeElapsed;
        int iteration;
#if BENCHMARK
        public static int particlesCount;
#endif

        void Awake()
        {
            gc_start_count_ = System.GC.CollectionCount(0 /* generation */);
        }

        IEnumerator Start()
        {
            DateTime then = DateTime.Now;

            while (true)
            {
                ++frameCount;
                timeElapsed += (float)(DateTime.Now - then).TotalSeconds;
                if (timeElapsed >= FPSCheckIntervalSecond)
                {
                    showingFPSValue = (timeElapsed * 1000.0f) / frameCount;
                    frameCount      = 0;
                    timeElapsed     = 0;

                    if (iteration++ > 7)
                    {
                        if (iteration % 50 == 0)
                        {
                            showingMinFPSValue = float.MaxValue;
                            showingMaxFPSValue = 0;
                        }

                        if (showingMinFPSValue > showingFPSValue) showingMinFPSValue = showingFPSValue;
                        if (showingMaxFPSValue < showingFPSValue) showingMaxFPSValue = showingFPSValue;
                    }
                }

                then = DateTime.Now;

                gc_count = System.GC.CollectionCount(0 /* generation */) - gc_start_count_;

                yield return null;
            }
        }
    }
}
