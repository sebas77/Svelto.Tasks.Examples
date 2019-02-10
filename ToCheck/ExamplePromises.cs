using System;
using System.Collections;
using System.Collections.Generic;
using Svelto.Tasks;
using Svelto.Tasks.Enumerators;
using Svelto.Tasks.Unity;
using UnityEngine;
using UnityEngine.Networking;

namespace Test.Editor
{
    public class ExamplePromises : MonoBehaviour
    {
        [TextArea]
        public string Notes = "This example shows how to use the promises-like features." + 
                              " Pay attention to how Stop a promise, catch a failure and set a race condition ";

        class ValueObject<T>
        {
            public ValueObject(T par)
            {
                target = par;
            }

            public object target;
        }

        void OnEnable()
        {
            UnityConsole.Clear();
        }
        // Use this for initialization
        void Start()
        {
            var task = TaskRunner.Instance.AllocateNewTaskRoutine();
            task.SetEnumerator(RunTasks(0.1f));
            task.Start(onStop: OnStop);
        }

        void OnStop()
        {
            Debug.LogWarning("oh oh, did't happen on time, let's try again");

            var task = TaskRunner.Instance.AllocateNewTaskRoutine();
            task.SetEnumerator(RunTasks(1000));
                task.Start(OnFail);
        }

        void OnFail(SveltoTaskException obj)
        {
            Debug.LogError("tsk tsk");
        }

        IEnumerator<TaskContract?> RunTasks(float timeout)
        {
            var enumerator = GetURLAsynchronously();

            //wait for one second (simulating async load) 
            yield return enumerator.Continue();
        
            string url = (string) enumerator.Current.Value.reference;

            var parallelTasks = new ParallelTaskCollection();

            //parallel tasks with race condition (timeout Breaks it)
            parallelTasks.Add(BreakOnTimeOut(timeout));
            parallelTasks.Add(new LoadSomething(new UnityWebRequest(url)).GetEnumerator());

            yield return parallelTasks.Continue();

            if ((Break)parallelTasks.Current == Break.It)
            {
                yield return Break.AndStop;

                throw new Exception("should never get here");
            }

            yield return new WaitForSecondsEnumerator(2).Continue();
        }

        IEnumerator<TaskContract?> GetURLAsynchronously()
        {
            yield return new WaitForSecondsEnumerator(1).Continue(); //well not real reason to wait, let's assume we were running a web service

            yield return new TaskContract("http://download.thinkbroadband.com/5MB.zip");
        }

        IEnumerator<TaskContract?> BreakOnTimeOut(float timeout) 
        {
            var time = DateTime.Now;
            yield return new WaitForSecondsEnumerator(timeout).Continue();
            Debug.Log("time passed: " + (DateTime.Now - time).TotalMilliseconds);

            yield return Break.It;

            //this is the inverse of the standard Promises race function, 
            //achieve the same result as it stops the parallel enumerator 
            //once hit
        }

        class LoadSomething : IEnumerable<TaskContract?>
        {
            public LoadSomething(UnityWebRequest wWW)
            {
                this.wWW = wWW;
            }

            public IEnumerator<TaskContract?> GetEnumerator()
            {
                Debug.Log("download started");

                yield return new ParallelTaskCollection(new[] { new UnityWebRequestEnumerator(wWW), PrintProgress(wWW) }).Continue();

                foreach (string s in wWW.GetResponseHeaders().Values)
                    Debug.Log(s);

                Debug.Log("Success! Let's throw an Exception to be caught by OnFail");

                throw new Exception("Dayyym");
            }

            IEnumerator<TaskContract?> PrintProgress(UnityWebRequest wWW)
            {
                while (wWW.isDone == false)
                {
                    Debug.Log(wWW.downloadProgress);

                    yield return null;
                }
            }

            UnityWebRequest wWW;
            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }
    }
}
