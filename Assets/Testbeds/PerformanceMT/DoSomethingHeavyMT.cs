using System.Collections.Generic;
using Svelto.Tasks;
using Svelto.Tasks.Lean;
using Svelto.Tasks.Unity;
using UnityEngine;

namespace PerformanceMT
{
    public class DoSomethingHeavyMT : MonoBehaviour
    {
        Vector2 direction;

        void Start()
        {
            direction = new Vector2(Mathf.Cos(Random.Range(0, 3.14f)) / 1000, Mathf.Sin(Random.Range(0, 3.14f) / 1000));
            _component = GetComponent<Renderer>();
        }

        void OnEnable()
        {
            //Start a task on the standard multithread scheduler, passing the standard update scheduler by parameter
            //to be used inside the task itself to start another task :)
            CalculateAndShowNumber(StandardSchedulers.updateScheduler).Run(StandardSchedulers.multiThreadScheduler);
        }

        /// <summary>
        /// CalculateAndShowNumber will run on another thread. This is a complex enumerator which won't simply
        /// return null, therefore it must follow the TaskContract signature, which enables some special features
        /// </summary>
        /// <param name="updateScheduler"></param>
        /// <returns></returns>
        IEnumerator<TaskContract> CalculateAndShowNumber(        
            UpdateMonoRunner<LeanSveltoTask<IEnumerator<TaskContract>>> updateScheduler)
        {
            var findPrimeNumber = FindPrimeNumber();
            var setColor = SetColor();

            while (true)
            {
                //it's possible to continue the execution of a new enumerator from the current runner
                yield return findPrimeNumber.Continue();

                //since it's not possible to use Unity functions outside the mainthread, the SetColor task must run
                //inside a runner that can schedule it on the unity main thread. The current thread will then
                //wait until the SetColor task is done. This waiting from a task running on another thread is called
                //continuation. The Run function returns a continuation wrapper and yielding is the same of writing:
                //var continuator = setColor.Run(updateScheduler);
                //while (continuator.MoveNext()) yield return null;
                yield return setColor.Run(updateScheduler);
            }
        }
        
        void Update()
        {
            transform.Translate(direction);
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

                _result = --a * 333; //the result can be processed

                yield return Break.It;
            }
        }

        IEnumerator<TaskContract> SetColor()
        {
            while (true)
            {
                _component.material.color = new Color(_result % 255 / 255f, (_result * _result % 255) / 255f,
                                                      (_result / 44) % 255 / 255f);

                yield return Break.It;
            }
        }

        Renderer _component;
        long     _result;
        
        static System.Random RND = new System.Random(); //not a problem, multithreaded coroutine are threadsafe within the same runner
    }
}
