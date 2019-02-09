using System.Collections;
using UnityEngine;

namespace PerformanceMT
{
    public class DoSomethingHeavy:MonoBehaviour
    {
        Vector2 direction;
        void Start()
        {
            StartCoroutine(CalculateAndShowNumber());
            direction = new Vector2(Mathf.Cos(Random.Range(0, 3.14f)) / 1000, Mathf.Sin(Random.Range(0, 3.14f) / 1000));
        }

        IEnumerator CalculateAndShowNumber()
        {
            while (true)
            {
                FindPrimeNumber();
                
                GetComponent<Renderer>().material.color = new Color((result % 255) / 255f, ((result * result) % 255) / 255f, ((result / 44) % 255) / 255f);

                yield return null;
            }
        }

        void Update()
        {
            transform.Translate(direction);
        }

        public void FindPrimeNumber()
        {
            int n = (RND.Next() % 16) + 1000;
                
            int count = 0;
            int a     = 2;
            while (count < n)
            {
                long b     = 2;
                int  prime = 1; // to check if found a prime
                while (b * b <= a)
                {
                    if (a % b == 0)
                    {
                        prime = 0;
                        break;
                    }

                    b++;
                }

                if (prime > 0)
                    count++;
                a++;
            }

            result = --a * 333; //the result can be processed
        }

        static System.Random RND = new System.Random(); //not a problem, multithreaded coroutine are threadsafe within the same runner
        int result;
    }
}
