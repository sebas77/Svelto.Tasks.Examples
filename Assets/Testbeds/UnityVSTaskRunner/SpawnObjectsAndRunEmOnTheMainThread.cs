using System.Collections;
using Svelto.Tasks;
using Svelto.Tasks.ExtraLean;
using Svelto.Tasks.Unity;
using UnityEngine;

namespace Test.Editor.UnityVSTaskRunner
{
    public class SpawnObjectsAndRunEmOnTheMainThread : MonoBehaviour 
    {
        [TextArea]
        public string Notes = "This example shows the difference between using the TaskRunner and the Monobehaviour StartCoroutine. Press a key to switch between the two.";
        // Use this for initialization
        void Start () 
        {
            TaskRunner.StopAndCleanupAllDefaultSchedulers();

            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
        
            parent1 = new GameObject();
            parent1.transform.parent = this.transform;

            Material matRed = new Material(Shader.Find("Standard"));

            matRed.color = Color.red;
            matRed.enableInstancing = true;

            _runner = new UpdateMonoRunner<ExtraLeanSveltoTask<TransformUpdate>, TimeSlicedRunningInfo>("test", new TimeSlicedRunningInfo(10));
            
            for (int i = 0; i < 15000; i++)
            {
                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.transform.parent = parent1.transform;    
                
                Destroy(sphere.GetComponent<SphereCollider>());

                sphere.GetComponent<Renderer>().sharedMaterial = matRed;
                
                new TransformUpdate(sphere.transform).RunOn(_runner);
            }
        }
        
        GameObject parent1;
        UpdateMonoRunner<ExtraLeanSveltoTask<TransformUpdate>, TimeSlicedRunningInfo> _runner;
    }

    struct TransformUpdate : IEnumerator
    {
        Transform _transform;
        Vector2 _direction;

        public TransformUpdate(Transform sphereTransform):this()
        {
            _transform = sphereTransform;
            _direction = new Vector2(Mathf.Cos(Random.Range(0, 3.14f)) / 1000, Mathf.Sin(Random.Range(0, 3.14f) / 1000));
        }

        public void Reset()
        {
            throw new System.NotImplementedException();
        }

        object IEnumerator.Current { get; }


        public bool MoveNext()
        {
            _transform.Translate(_direction);

            return true;
        }
    }
}
