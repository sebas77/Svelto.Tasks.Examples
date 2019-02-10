using System.Collections;
using System.Collections.Generic;
using Svelto.Tasks;
using Svelto.Tasks.Enumerators;
using Svelto.Tasks.Unity;
using UnityEngine;
using UnityEngine.Networking;

namespace Test.Editor
{
    public class ExampleParallelTasks : MonoBehaviour 
    {
        int i;

        [TextArea]
        public string Notes = "This example shows how to run different types of tasks in Parallel";

        void OnEnable()
        {
            UnityConsole.Clear();
        }

        // Use this for initialization
        void Start () 
        {
            Application.targetFrameRate = 60;

            Debug.Log("Set frame rate to 60fps");
		
            ParallelTaskCollection pt = new ParallelTaskCollection();
            SerialTaskCollection	st = new SerialTaskCollection();
		
            st.Add(Print("s1"));
            st.Add(DoSomethingAsynchonously());
            st.Add(Print("s3"));
		
            pt.Add(Print("1"));
            pt.Add(Print("2"));
            pt.Add(Print("3"));
            pt.Add(Print("4"));
            pt.Add(Print("5"));
            pt.Add(st);
            pt.Add(Print("6"));
            pt.Add(WWWTest ());
            pt.Add(Print("7"));
            pt.Add(Print("8"));

            pt.onComplete += () =>
                             {
                                 Application.targetFrameRate = -1;
                                 Debug.Log("Unlock framerate");
                             };
			
            pt.Start();
        }
	
        IEnumerator<TaskContract?> Print(string i)
        {
            Debug.Log(i);
            yield return null;
        }
	
        IEnumerator<TaskContract?> DoSomethingAsynchonously()  //this can be awfully slow, I suppose it is synched with the frame rate
        {
            for (i = 0; i < 500; i++)
                yield return i; //it will continue on the next frame
		
            Debug.Log("index " + i);
        }
	
        IEnumerator<TaskContract?> WWWTest()
        {
            UnityWebRequest www = new UnityWebRequest("www.google.com");
		
            yield return new UnityWebRequestEnumerator(www).Continue();
		
            Debug.Log("www done:" + www.GetResponseHeaders());
        }
    }
}
