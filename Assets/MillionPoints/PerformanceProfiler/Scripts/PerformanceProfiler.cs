using System.Collections;
using System;
using UnityEngine;

namespace PerformanceCheker
{

    public class PerformanceProfiler : MonoBehaviour
    {
        public int GCcount
        {
            get
            {
                return gc_count;
            }
        }

        public int CurrentFPS
        {
            get
            {
                return (int)showingFPSValue;
            }
        }
        
        public int MaxFPS
        {
            get
            {
                return (int)showingMaxFPSValue;
            }
        }
        
        public int MinFPS
        {
            get
            {
                return (int)showingMinFPSValue;
            }
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

        // Update is called once per frame
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