using System.Collections;
using System.Collections.Generic;
using Svelto.Tasks;
using Svelto.Tasks.Enumerators;
using Svelto.Tasks.Unity;
using UnityEngine;
using UnityEngine.Networking;

namespace Test.Editor
{
    class LoadSomething : IEnumerable<TaskContract?>
    {
        public LoadSomething(UnityWebRequest wWW)
        {
            this.wWW = wWW;
        }

        public IEnumerator<TaskContract?> GetEnumerator()
        {
            yield return new UnityWebRequestEnumerator(wWW).Continue();

            foreach (var s in wWW.GetResponseHeaders())
                Debug.Log(s);
        }

        UnityWebRequest wWW;
        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    public class ExampleParallelTasksManaged : MonoBehaviour 
    {
        [TextArea]
        public string Notes = "This example shows how to run different types of tasks in Parallel with the TaskRunner " +
                              "(pressing any key will pause the task)";

        void OnEnable()
        {
            UnityConsole.Clear();
        }

        // Use this for initialization
        void Start () 
        {
            var pt = new ParallelTaskCollection();
            var st = new SerialTaskCollection();
        
            st.Add(Print("s1"));
            st.Add(Print("s2"));
            st.Add(pt);
            st.Add(Print("s3"));
            st.Add(Print("s4"));
        
            pt.Add(Print("1"));
            pt.Add(Print("2"));

            //only the task runner can actually handle parallel tasks
            //that return Unity operations (when unity compatible
            //schedulers are used)
            pt.Add(UnityAsyncOperationsMustNotBreakTheParallelism());
            pt.Add(UnityYieldInstructionsMustNotBreakTheParallelism());

            pt.Add(new LoadSomething(new UnityWebRequest("www.google.com")).GetEnumerator()); //obviously the token could be passed by constructor, but in some complicated situations, this is not possible (usually while exploiting continuation)
            pt.Add(new LoadSomething(new UnityWebRequest("http://download.thinkbroadband.com/5MB.zip")).GetEnumerator());
            pt.Add(new LoadSomething(new UnityWebRequest("www.ebay.com")).GetEnumerator());
            pt.Add(Print("3"));
            pt.Add(Print("4"));
            pt.Add(Print("5"));
            pt.Add(Print("6"));
            pt.Add(Print("7"));
            
            st.Start();
        }

        IEnumerator<TaskContract?> UnityAsyncOperationsMustNotBreakTheParallelism()
        {
            Debug.Log("start async operation");
            var res = Resources.LoadAsync("image.jpg");
            yield return new AsyncOperationEnumerator(res).Continue();
            Debug.Log("end async operation " + res.progress);
        }

        IEnumerator<TaskContract?> UnityYieldInstructionsMustNotBreakTheParallelism()
        {
            Debug.Log("start yield instruction");
            yield return new WaitForSecondsEnumerator(2).Continue();
            Debug.Log("end yield instruction");
        }

        void Update()
        {
            if (Input.anyKeyDown)
                if (_paused == false)
                {
                    Debug.LogWarning("Paused!");
                    TaskRunner.Instance.PauseAllTasks();
                    _paused = true;
                }
                else
                {
                    Debug.LogWarning("Resumed!");
                    _paused = false;
                    TaskRunner.Instance.ResumeAllTasks();
                }
        }

        IEnumerator<TaskContract?> Print(string i)
        {
            Debug.Log(i);

            yield break;
        }

        bool _paused;
    }
}