using PerformanceMT;
using Svelto.Tasks;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

namespace Test.Editor.PerformanceMT
{
    public class SpawnObjectsMT : MonoBehaviour
    {
        [TextArea]
        public string Notes = "Enable this to run the example on another thread.";
        
        // Use this for initialization
        void OnEnable()
        { 
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
            
            TaskRunner.StopAndCleanupAllDefaultSchedulers();
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
    }
}
