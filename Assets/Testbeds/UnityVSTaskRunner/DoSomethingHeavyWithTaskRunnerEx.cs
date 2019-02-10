using System.Collections;
using Svelto.Tasks.ExtraLean;
using Svelto.Tasks.Unity;
using UnityEngine;

namespace Test.Editor.UnityVSTaskRunner
{
    /// <summary>
    /// This experiments is to show the fastest way to run tasks in Svelto.Tasks. In this case UpdateIt is implemented
    /// through a struct, so that the tasks inside the runner are laid out sequentially in memory, enabling
    /// cache friendly code. Unluckily in this case it doesn't make much sense because _transform is still
    /// a reference and therefore it breaks the cache (it's still a tiny bit faster tho)
    /// </summary>
    public class DoSomethingHeavyWithTaskRunnerEx : MonoBehaviour
    {
        void Awake()
        {
            _direction = new Vector2(Mathf.Cos(Random.Range(0, 3.14f)) / 1000, Mathf.Sin(Random.Range(0, 3.14f) / 1000));            
        }

        void OnEnable()
        {
            if (_runner == null)
                _runner = new ExtraLeanUpdateMonoRunner<UpdateIt>("test");
            
            new UpdateIt(transform, _direction).RunOn(_runner);
        }
        
      
        void OnDisable()
        {
            if (_runner != null)
            {
                _runner.Dispose();
                _runner = null;
            }
        }

        struct UpdateIt:IEnumerator
        {
            public bool MoveNext()
            {
                _transform.Translate(_dir);

                return true;
            }

            public void Reset()
            {
                throw new System.NotImplementedException();
            }

            public object Current
            {
                get => throw new System.NotImplementedException();
                set => throw new System.NotImplementedException();
            }
            
            Transform _transform;
            Vector3 _dir;

            public UpdateIt(Transform transform, Vector3 dir)
            {
                _transform = transform;
                _dir = dir;
            }
        }
        
        Vector2                                       _direction;
        static ExtraLeanUpdateMonoRunner<UpdateIt> _runner;
    }
}
