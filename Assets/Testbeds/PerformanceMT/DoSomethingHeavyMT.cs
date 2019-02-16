using UnityEngine;

namespace PerformanceMT
{
    public class DoSomethingHeavyMT : MonoBehaviour
    {
        void Awake()
        {
            _transform = transform;
        }

        void Start()
        {
            _direction = new Vector2(Mathf.Cos(Random.Range(0, 3.14f)) / 1000,
                                    Mathf.Sin(Random.Range(0, 3.14f) / 1000));
        }

        void Update()
        {
            _transform.Translate(_direction);
        }

        Vector2                                                   _direction;
        Transform                                                 _transform;
    }
}