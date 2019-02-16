using System.Collections;
using System.Collections.Generic;
using PerformanceMT;
using Svelto.Tasks;
using Svelto.Tasks.Enumerators;
using Svelto.Tasks.Lean;
using Svelto.Tasks.Lean.Unity;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using Random = System.Random;

namespace Test.Editor.PerformanceMT
{
    public class SpawnObjectsMT : MonoBehaviour
    {
        [TextArea]
        public string Notes = "Enable this to run the example on another thread.";
        
        // Use this for initialization
        void OnEnable()
        { 
            _unityThreadRunner = new UpdateMonoRunner<LocalFunctionEnumerator<int>>("SpawnObjectsMT");
#if UNITY_EDITOR            
            EditorApplication.pauseStateChanged += LogPauseState;
#endif            
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;

            for (int i = 0; i < 150; i++)
            {
                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);

                sphere.AddComponent<DoSomethingHeavyMT>();

                sphere.transform.parent = this.transform;

                CalculateAndShowNumber(sphere.GetComponent<Renderer>()).RunOn(StandardSchedulers.multiThreadScheduler);
            }
        }
#if UNITY_EDITOR
        void LogPauseState(PauseState obj)
        {
            if (obj == PauseState.Paused)
                TaskRunner.Pause();
            else
                TaskRunner.Resume();
        }
#endif        

        void OnDisable()
        {
            foreach (Transform trans in transform)
            {
                Destroy(trans.gameObject);
            }
            
            _unityThreadRunner.Dispose();
        }
        
        void Update()
        {
            if (Input.anyKeyDown)
            {
                GetComponent<SpawnObjectsMT>().enabled = false;
                GetComponent<SpawnObjects>().enabled = true;
            }
        }

        void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus == true)
                TaskRunner.Pause();
            else
                TaskRunner.Resume();
        }

        UpdateMonoRunner<LocalFunctionEnumerator<int>> _unityThreadRunner;
        
        /// <summary>
        ///     CalculateAndShowNumber runs on another thread. This is a complex enumerator which won't simply
        ///     return null, therefore it must follow the TaskContract signature, which enables some special features
        /// </summary>
        /// <param name="updateScheduler"></param>
        /// <returns></returns>
        IEnumerator<TaskContract> CalculateAndShowNumber(Renderer _component)
        {
            var findPrimeNumber = FindPrimeNumber();
            
            bool SetColor(ref int color)
            {
                _component.material.color = new Color(color % 255 / 255f, color * color % 255 / 255f,
                                                      color / 44 % 255 / 255f);

                return false;
            }

            while (true)
            {
                //passing the execution to an enumerator is possible only through the Continue() extension method.
                yield return findPrimeNumber.Continue();

                //since it's not possible to use Unity functions outside the mainthread, the SetColor task must run
                //inside a runner on the unity main thread. The current thread will then
                //wait until the SetColor task is done. This waiting of a task running on another runner is called
                //continuation. The Run function returns a continuation wrapper and yielding is the same of writing:
                //var continuator = setColor.Run(updateScheduler); while (continuator.MoveNext()) yield return null;
                yield return new LocalFunctionEnumerator<int>(SetColor, findPrimeNumber.Current.ToInt()).RunOn(_unityThreadRunner);
            }
        }

        IEnumerator<TaskContract> FindPrimeNumber()
        {
            while (true)
            {
                int n = (RND.Next() % 16) + 1000;
                
                int count = 0;
                int a     = 2;
                while (count < n)
                {
                    long b     = 2;
                    int  prime = 1; // to check if found a prime
                    while (b * b <= a)
                    {
                        if (a % b == 0)
                        {
                            prime = 0;
                            break;
                        }

                        b++;
                    }

                    if (prime > 0)
                        count++;
                    a++;
                }

                yield return --a * 333; //the result can be processed
            }
        }
        
        readonly Random
            RND = new Random(); //not a problem, multithreaded coroutine are threadsafe within the same runner
    }
}
