using System.Collections.Generic;
using Svelto.Tasks;
using Svelto.Tasks.Enumerators;
using Svelto.Tasks.Lean;
using UnityEngine;

namespace Testbeds.TimeSlice
{
    public class TimeSlice : MonoBehaviour
    {
        void Start()
        {
            RunTest().RunOn(StandardSchedulers.multiThreadScheduler);
        }

        IEnumerator<TaskContract> RunTest()
        {
            while (true)
            {
        //        yield return new WaitForSecondsEnumerator
                
                yield return Yield.It;
            }
        }
    }
}