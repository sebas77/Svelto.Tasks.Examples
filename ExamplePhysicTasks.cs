using System;
using System.Collections.Generic;
using Svelto.Tasks;
using Svelto.Tasks.Unity;
using UnityEngine;

namespace Test.Editor
{
    public class ExamplePhysicTasks : MonoBehaviour 
    {
        [TextArea]
        public string Notes = "This example shows how to run a task on the physic scheduler.";

        void OnEnable () 
        {
            UnityConsole.Clear();

            Time.fixedDeltaTime = 0.5f;

            PrintTime().Run(StandardSchedulers.physicScheduler);
        }

        void OnDisable()
        {
            StandardSchedulers.physicScheduler.StopAllCoroutines();
        }

        IEnumerator<TaskContract?> PrintTime()
        {
            var timeNow = DateTime.Now;
            while (true)
            {
                Debug.Log("FixedUpdate time :" + (DateTime.Now - timeNow).TotalSeconds);
                timeNow = DateTime.Now;
                yield return null;
            }
        }
    }
}
