using System.Collections;
using System.Collections.Generic;
using Assets;
using Svelto.Tasks;
using Svelto.Tasks.Lean;
using Svelto.Tasks.Lean.Unity;
using UnityEngine;

namespace Test.Editor.UnityVSTaskRunner
{
    public class SpawnObjects : MonoBehaviour 
    {
        [TextArea]
        public string Notes = "This example shows the difference between using the TaskRunner and the Monobehaviour StartCoroutine. Press a key to switch between the two.";
        // Use this for initialization
        void Start () 
        {
            TaskRunner.StopAndCleanupAllDefaultSchedulers();

            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
            Material matYellow = new Material(Shader.Find("Legacy Shaders/Diffuse"));
            Material matRed    = new Material(matYellow);
            Material matGreen  = new Material(matYellow);

            matYellow.color = Color.yellow;
            matRed.color    = Color.red;
            matGreen.color  = Color.green;
            
            parents = new GameObject[2];
            parents[0] = new GameObject();
            parents[0].transform.parent = this.transform;

            ///
            ///Initialize 15k Gameobjects and add a component as standard in Unity
            /// 
            for (int i = 0; i < 15000; i++)
            {
                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.transform.parent = parents[0].transform;
                
                Destroy(sphere.GetComponent<SphereCollider>());

                sphere.AddComponent<DoSomethingHeavyWithUnity>();
                sphere.GetComponent<Renderer>().material = new Material(matYellow);
            }

            var parent2 = parents[1] = new GameObject();
            parent2.transform.parent = this.transform;
            
            //
            //Initialize 15k GameObject and push 15k svelto tasks. Note that in ECS you probably
            //would have ended up pushing one task that iterates over 15k objects (or better structs)
            //but this wouldn't have been fair for this comparison
            //
            
            for (int i = 0; i < 15000; i++)
            {
                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.transform.parent = parent2.transform;    
                
                Destroy(sphere.GetComponent<SphereCollider>());

                sphere.GetComponent<Renderer>().material = new Material(matRed);
            
                //Naive version, it's OK in most of the cases when is called during initialization time
                //as this allocates! It runs on the "standard" schedulers
                UpdateIt(sphere.transform).RunOn(StandardSchedulers.updateScheduler);
                //Special version, the runner can run dedicated tasks as struct. Zero allocation!
                new UpdateItStruct(sphere.transform).RunOn(_fastRunner);

            }
            
            _fastRunner.Pause();
            //the standard schedulers can be controlled through the TaskRunner class
            TaskRunner.Pause();
            
            text = GetComponentInChildren<UnityEngine.UI.Text>();
            text.text = "Unity coroutine Enabled";
            
            parents[1].SetActive(false);
        }

        // Update is called once per frame
        void Update()
        {
            if (Input.anyKeyDown)
            {
                var index0 = ++index % 3;

                switch (index0)
                {
                        case 0:
                        {
                            parents[0].SetActive(true);
                            parents[1].SetActive(false);
                            
                            _fastRunner.Pause();
                            TaskRunner.Pause();

                            text.text = "Unity coroutine Enabled";

                            break;
                        }
                        case 1:
                        {
                            parents[0].SetActive(false);
                            parents[1].SetActive(true);
                            
                            _fastRunner.Pause();
                            TaskRunner.Resume();

                            text.text = "TaskRunner coroutine Enabled";

                            break;
                        }
                        case 2:
                        {
                            parents[0].SetActive(false);
                            parents[1].SetActive(true);
                            
                            _fastRunner.Resume();
                            TaskRunner.Pause();

                            text.text = "TaskRunner coroutine structs Enabled";

                            break;
                        }
                }
            }
        }

        void OnEnable()
        {
            _fastRunner = new UpdateMonoRunner<UpdateItStruct>("SpawnObject");
        }
        
        void OnDisable()
        {
            _fastRunner.Dispose();
        }

        int                 index;
        GameObject[]        parents;
        GameObject          nextParent;
        UnityEngine.UI.Text text;
        
        UpdateMonoRunner<UpdateItStruct> _fastRunner;
        
        //this runs for ever! However we can control its execution in many ways. For this example
        //we pause the runner that runs it.
        IEnumerator<TaskContract> UpdateIt(Transform transform)
        {
            var direction = new Vector3(Mathf.Cos(Random.Range(0, 3.14f)) / 1000, Mathf.Sin(Random.Range(0, 3.14f) / 1000));
            
            while (true) 
            {
                var transformPosition = transform.position;
                transformPosition.x += direction.x;
                transformPosition.y += direction.y;
                transform.position =  transformPosition;

                yield return Yield.It;
            }
        }
        
        struct UpdateItStruct: IEnumerator<TaskContract>
        {
            public bool MoveNext()
            {
                var transformPosition = _transform.position;
                transformPosition.x += _dir.x;
                transformPosition.y += _dir.y;
                _transform.position =  transformPosition;

                return true;
            }

            public void Reset() { throw new System.NotImplementedException(); }

            TaskContract IEnumerator<TaskContract>.Current => throw new System.NotImplementedException();

            public object Current { get => throw new System.NotImplementedException(); }

            readonly Transform _transform;
            readonly Vector3   _dir;

            public UpdateItStruct(Transform transform)
            {
                _transform = transform;
                _dir = new Vector2(Mathf.Cos(Random.Range(0, 3.14f)) / 1000, Mathf.Sin(Random.Range(0, 3.14f) / 1000));
            }

            public void Dispose()
            {
                throw new System.NotImplementedException();
            }
        }
    }
}
