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
        
            parent1 = new GameObject();
            parent1.transform.parent = this.transform;
            parent1.SetActive(false);

            Material matYellow = new Material(Shader.Find("Legacy Shaders/Diffuse"));
            Material matRed = new Material(Shader.Find("Legacy Shaders/Diffuse"));

            matYellow.color = Color.yellow;
            matRed.color = Color.red;

            for (int i = 0; i < 15000; i++)
            {
                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.transform.parent = parent1.transform;
                
                Destroy(sphere.GetComponent<SphereCollider>());

                sphere.AddComponent<DoSomethingHeavyWithUnity>();
                sphere.GetComponent<Renderer>().material = new Material(matYellow);
            }

            parent2 = new GameObject();
            parent2.transform.parent = this.transform;
            
            for (int i = 0; i < 15000; i++)
            {
                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.transform.parent = parent2.transform;    
                
                Destroy(sphere.GetComponent<SphereCollider>());

                sphere.AddComponent<DoSomethingHeavyWithTaskRunner>();
                sphere.GetComponent<Renderer>().material = new Material(matRed);
            }

            var texts = GetComponentsInChildren<UnityEngine.UI.Text>();
            text = texts[0];
            text.text = "TaskRunner coroutine Enabled";
        }

        // Update is called once per frame
        void Update()
        {
            if (Input.anyKeyDown)
            {
                parent1.SetActive(!parent1.activeSelf);
                parent2.SetActive(!parent2.activeSelf);

                if (parent1.activeInHierarchy == true)
                    text.text = "Unity coroutine Enabled";
                else
                    text.text = "TaskRunner coroutine Enabled";
            }
        }

        GameObject parent1;
        GameObject parent2;
        UnityEngine.UI.Text text;
    }
}
