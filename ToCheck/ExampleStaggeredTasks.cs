using System.Collections.Generic;
using UnityEngine;
using Svelto.Tasks.Unity;

public class ExampleStaggeredTasks : MonoBehaviour 
{
	const int MaxTasksPerFrame = 3;
    [TextArea]
    public string Notes = "This example shows how to run tasks spreaded over several frames.";

    StaggeredMonoRunner _runner;

    void OnEnable () 
	{
        UnityConsole.Clear();

        _runner = new StaggeredMonoRunner("StaggeredRunner", MaxTasksPerFrame);

        for (int i = 0; i < 300; i++)
            PrintFrame().Run(_runner);
	}

    void OnDisable()
    {
        _runner.StopAllCoroutines();
    }

    IEnumerator<TaskContract?> PrintFrame()
	{
        Debug.Log(Time.frameCount);
        
        yield break;
	}
}
