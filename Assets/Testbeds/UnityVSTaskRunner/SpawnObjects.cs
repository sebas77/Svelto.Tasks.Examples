using Assets;
using Svelto.Tasks;
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
            parents = new GameObject[3];
        
            parents[0] = new GameObject();
            parents[0].transform.parent = this.transform;

            Material matYellow = new Material(Shader.Find("Legacy Shaders/Diffuse"));
            Material matRed = new Material(matYellow);
            Material matGreen = new Material(matYellow);

            matYellow.color = Color.yellow;
            matRed.color = Color.red;
            matGreen.color = Color.green;

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
            
            for (int i = 0; i < 15000; i++)
            {
                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.transform.parent = parent2.transform;    
                
                Destroy(sphere.GetComponent<SphereCollider>());

                sphere.AddComponent<DoSomethingHeavyWithTaskRunner>();
                sphere.GetComponent<Renderer>().material = new Material(matRed);
            }
            
            var parent3 = parents[2] = new GameObject();
            parent3.transform.parent = this.transform;
            
            for (int i = 0; i < 15000; i++)
            {
                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.transform.parent = parent3.transform;    
                
                Destroy(sphere.GetComponent<SphereCollider>());

                sphere.AddComponent<DoSomethingHeavyWithTaskRunnerEx>();
                sphere.GetComponent<Renderer>().material = new Material(matGreen);
            }

            text = GetComponentInChildren<UnityEngine.UI.Text>();
            text.text = strings[0];
            
            parents[1].SetActive(false);
            parents[2].SetActive(false);
        }

        // Update is called once per frame
        void Update()
        {
            if (Input.anyKeyDown)
            {
                parents[0].SetActive(false);
                parents[1].SetActive(false);
                parents[2].SetActive(false);

                var index0 = ++index % 3;
                nextParent = parents[index0];
                nextParent.SetActive(true);

                text.text = strings[index0];
            }
        }

        int index;
        GameObject[] parents;
        GameObject nextParent;
        UnityEngine.UI.Text text;

        string[] strings =
        {
            "Unity coroutine Enabled", "TaskRunner coroutine Enabled", "TaskRunner coroutine structs Enabled"
        };
    }
}
