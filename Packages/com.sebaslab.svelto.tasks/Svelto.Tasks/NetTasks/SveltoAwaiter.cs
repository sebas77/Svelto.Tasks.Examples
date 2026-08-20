using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Svelto.Tasks.Internal;

namespace Svelto.Tasks.Lean
{
    public readonly struct ValueTaskRunnerAwaiter : ICriticalNotifyCompletion
    {
        readonly SteppableRunner _runner;
        readonly ValueTaskAwaiter _taskAwaiter;

        public ValueTaskRunnerAwaiter(ValueTask task, SteppableRunner runner)
        {
            _taskAwaiter = task.GetAwaiter();
            _runner = runner;
        }

        public bool IsCompleted => _taskAwaiter.IsCompleted;
        public void GetResult() => _taskAwaiter.GetResult();
        public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);

        public void UnsafeOnCompleted(Action continuation)
        {
            // post the continuation into the runner as a small enumerator
            var continuationEnumerator = ContinuationEnumeratorPool.RetrieveFromPool();
            continuationEnumerator.SetContinuation(continuation);

            new LeanSveltoTask<IEnumerator<TaskContract>>().Run(_runner, continuationEnumerator);
        }

        public ValueTaskRunnerAwaiter GetAwaiter() => this;
    }
    
    public readonly struct TaskRunnerAwaiter : ICriticalNotifyCompletion
    {
        readonly SteppableRunner _runner;
        readonly TaskAwaiter _taskAwaiter;

        public TaskRunnerAwaiter(Task task, SteppableRunner runner)
        {
            _taskAwaiter = task.GetAwaiter();
            _runner = runner;
        }

        public bool IsCompleted => _taskAwaiter.IsCompleted;
        public void GetResult() => _taskAwaiter.GetResult();
        public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);

        public void UnsafeOnCompleted(Action continuation)
        {
            // post the continuation into the runner as a small enumerator
            var continuationEnumerator = ContinuationEnumeratorPool.RetrieveFromPool();
            continuationEnumerator.SetContinuation(continuation);

            new LeanSveltoTask<IEnumerator<TaskContract>>().Run(_runner, continuationEnumerator);
        }

        public TaskRunnerAwaiter GetAwaiter() => this;
    }

// extension method to get our awaiter
    public static class SveltoAwaiterExtensions
    {
        public static ValueTaskRunnerAwaiter RunOn(this ValueTask task, SteppableRunner runner)
        {
            return new ValueTaskRunnerAwaiter(task, runner);
        }
        
        public static TaskRunnerAwaiter RunOn(this Task task, SteppableRunner runner)
        {
            return new TaskRunnerAwaiter(task, runner);
        }
    }
}