using System.Collections.Generic;
using Svelto.Tasks;
using Svelto.Tasks.Enumerators;
using Svelto.Tasks.Lean;
using Svelto.Tasks.Unity;
using UnityEngine;
using Random = System.Random;

namespace PerformanceMT
{
    public class DoSomethingHeavyMT : MonoBehaviour
    {
        void Awake()
        {
            _transform = transform;
        }

        void OnDisable()
        {
            if (_unityThreadRunner != null)
            {
                _unityThreadRunner.Dispose();
                _unityThreadRunner = null;
            }
        }

        void Start()
        {
            _direction = new Vector2(Mathf.Cos(UnityEngine.Random.Range(0, 3.14f)) / 1000,
                                    Mathf.Sin(UnityEngine.Random.Range(0, 3.14f) / 1000));
            
            _component = GetComponent<Renderer>();
        }

        void OnEnable()
        {
            if (_unityThreadRunner	== null)
                _unityThreadRunner = new UpdateMonoRunner<LeanSveltoTask<LocalFunctionEnumerator>>("main thread");
            //Start a task on the standard multithread scheduler
            CalculateAndShowNumber().RunOn(StandardSchedulers.multiThreadScheduler);
        }

        /// <summary>
        ///     CalculateAndShowNumber runs on another thread. This is a complex enumerator which won't simply
        ///     return null, therefore it must follow the TaskContract signature, which enables some special features
        /// </summary>
        /// <param name="updateScheduler"></param>
        /// <returns></returns>
        IEnumerator<TaskContract> CalculateAndShowNumber()
        {
            var findPrimeNumber = FindPrimeNumber();

            bool SetColor()
            {
                _component.material.color = new Color(_result % 255 / 255f, _result * _result % 255 / 255f,
                                                      _result / 44 % 255 / 255f);

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
                yield return new LocalFunctionEnumerator(SetColor).RunOn(_unityThreadRunner);
            }
        }

        void Update()
        {
            _transform.Translate(_direction);
        }

        IEnumerator<TaskContract> FindPrimeNumber()
        {
            while (true)
            {
                var n = RND.Next() % 16 + 1000;

                var count = 0;
                var a     = 2;
                while (count < n)
                {
                    long b     = 2;
                    var  prime = 1; // to check if found a prime
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

                _result = --a * 333; //the result can be processed

                yield return Break.It;
            }
        }
        
        static readonly Random
            RND = new Random(); //not a problem, multithreaded coroutine are threadsafe within the same runner

        Renderer                                                  _component;
        long                                                      _result;
        UpdateMonoRunner<LeanSveltoTask<LocalFunctionEnumerator>> _unityThreadRunner;
        Vector2                                                   _direction;
        Transform                                                 _transform;
    }
}