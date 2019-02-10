using System.Collections;
using System.Collections.Generic;
using Svelto.Tasks;
using Svelto.Tasks.Enumerators;
using Svelto.Tasks.Unity;
using UnityEngine;
using UnityEngine.Networking;

namespace Test.Editor
{
    public class ExampleSerialTasks : MonoBehaviour 
    {
        [TextArea]
        public string Notes = "This example shows how to run different types of tasks in Serial with the TaskRunner";

        void OnEnable()
        {
            UnityConsole.Clear();
        }

        void Start () 
        {
            SerialTaskCollection st = new SerialTaskCollection();
		
            st.Add(Print(1));
            st.Add(Print(2));
            st.Add(DoSomethingAsynchonously(1));
            st.Add(Print(3));
            st.Add(WaitForSecondsTest());
            st.Add(Print(4));
            st.Add(WWWTest ());
            st.Add(Print(5));
            st.Add(Print(6));

            st.Start();
        }
	
        IEnumerator<TaskContract?> Print(int i)
        {
            Debug.Log(i);
            yield return null;
        }
	
        IEnumerator<TaskContract?> DoSomethingAsynchonously(float time)
        {
            yield return new WaitForSecondsEnumerator(time).Continue();
		
            Debug.Log("waited " + time);
        }
	
        IEnumerator<TaskContract?> WWWTest()
        {
            UnityWebRequest www = new UnityWebRequest("www.google.com");
		
            yield return new UnityWebRequestEnumerator(www).Continue();
		
            Debug.Log("www done:" + www.GetResponseHeaders());
        }

        IEnumerator<TaskContract?> WaitForSecondsTest()
        {
            int counter = 0;
            while (counter < 2)
            {
                Debug.Log("TestTwice Loop: Time " + Time.time + " C: " + counter);
                counter++;

                yield return new WaitForSecondsEnumerator(1f).Continue();
            }
        }
    }
}
