using Svelto.Tasks.Enumerators;
using Svelto.Tasks.ExtraLean;
using Svelto.Tasks.Unity;
using UnityEngine;

namespace Test.Editor.UnityVSTaskRunner
{
    /// <summary>
    /// unluckily the LocalFunctionEnumerator do not perform well therefore I may drop this pattern
    /// </summary>
    public class DoSomethingHeavyWithTaskRunnerLocal : MonoBehaviour
    {
        void Awake()
        {
            _direction = new Vector2(Mathf.Cos(Random.Range(0, 3.14f)) / 1000, Mathf.Sin(Random.Range(0, 3.14f) / 1000));            
        }

        void OnEnable()
        {
            if (_runner == null)
                _runner = new ExtraLeanUpdateMonoRunner<LocalFunctionEnumerator>("test");
            
            _break = false;

            bool update()
            {
                transform.Translate(_direction);

                return true;
            }
            
            new LocalFunctionEnumerator(() => update()).RunOn(_runner);
        }
        
      
        void OnDisable()
        {
            if (_runner != null)
            {
                _runner.Dispose();
                _runner = null;
            }

            _break = true;
        }

        bool                                                      _break;
        Vector2                                                   _direction;
        static ExtraLeanUpdateMonoRunner<LocalFunctionEnumerator> _runner;
    }
}
