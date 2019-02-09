using System.Collections;
using Svelto.Tasks.ExtraLean;
using UnityEngine;

namespace Test.Editor.UnityVSTaskRunner
{
    public class DoSomethingHeavyWithTaskRunner : MonoBehaviour
    {
        bool    _break;
        Vector2 _direction;

        void Start()
        {
            _direction = new Vector2(Mathf.Cos(Random.Range(0, 3.14f)) / 1000, Mathf.Sin(Random.Range(0, 3.14f) / 1000));            
        }

        void OnEnable()
        {
            _break = false;
            
            //Run() is the simplest way to Run an IEnumerator using Svelto.Tasks
            UpdateIt(transform).Run();
        }
        
        IEnumerator UpdateIt(Transform transform)
        {
            while (_break == false) 
            {
                transform.Translate(_direction);

                yield return null;
            }
        }
      
        void OnDisable()
        {
            _break = true;
        }
    }
}
