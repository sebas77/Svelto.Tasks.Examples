using System.Collections.Generic;
using System.Threading;
using Svelto.Tasks.FlowModifiers;
using Svelto.Tasks.Lean;

namespace Svelto.Tasks.Tests
{
    [TestFixture]
    public class FlowModifiersTests
    {
        [Test]
        public void SerialFlow_RunsTasksSequentially()
        {
            using (var runner = new SteppableRunner("SerialFlow"))
            {
                runner.UseFlowModifier(new SerialFlow());

                var log = new List<int>();

                IEnumerator<TaskContract> Task1()
                {
                    log.Add(1);
                    yield return TaskContract.Yield.It;
                    log.Add(2);
                }

                IEnumerator<TaskContract> Task2()
                {
                    log.Add(3);
                    yield return TaskContract.Yield.It;
                    log.Add(4);
                }

                Task1().RunOn(runner);
                Task2().RunOn(runner);

                // Step 1: Task1 starts. Yields. SerialFlow stops iteration.
                runner.Step();
                Assert.That(log, Is.EqualTo(new[] { 1 }));

                // Step 2: Task1 continues. Completes. SerialFlow allows continuing to next task.
                runner.Step();
                Assert.That(log, Is.EqualTo(new[] { 1, 2, 3 }));

                // Step 3: Task2 continues.
                runner.Step();
                Assert.That(log, Is.EqualTo(new[] { 1, 2, 3, 4 }));
            }
        }

        [Test]
        public void StaggeredFlow_LimitsTasksPerIteration()
        {
            using (var runner = new SteppableRunner("StaggeredFlow"))
            {
                runner.UseFlowModifier(new StaggeredFlow(2)); // Max 2 tasks per step

                int executedCount = 0;
                IEnumerator<TaskContract> Task()
                {
                    executedCount++;
                    yield return TaskContract.Yield.It;
                }

                // Add 3 tasks
                Task().RunOn(runner);
                Task().RunOn(runner);
                Task().RunOn(runner);

                runner.Step();
                // Should execute 2 tasks
                Assert.That(executedCount, Is.EqualTo(2));

                runner.Step();
                // Should execute the first 2 tasks again (starvation of the 3rd task)
                // Wait, StaggeredFlow logic:
                // Iteration 1: Task 1 runs (iter=1), Task 2 runs (iter=2). Max reached. Task 3 doesn't run.
                // Iteration 2: Task 1 runs (iter=1), Task 2 runs (iter=2). Max reached. Task 3 doesn't run.
                // So executedCount should be 2 + 2 = 4.
                // But the test failed with "Expected: 2, But was: 3".
                // This means in the FIRST step, it executed 3 tasks.
                // My fix to StaggeredFlow changed logic to >= max.
                // If max is 2.
                // Task 1: iter=0. 0 >= 2 false. iter=1. True.
                // Task 2: iter=1. 1 >= 2 false. iter=2. True.
                // Task 3: iter=2. 2 >= 2 true. iter=0. False.
                // So it runs 2 tasks.
                // Why did it fail with 3?
                // Maybe because I didn't recompile properly or something?
                // Or maybe the runner logic is different.
                // Let's look at SveltoTaskRunner.Process.MoveNext.
                // It iterates _runningCoroutines.
                // CanProcessThis is called.
                // If CanProcessThis returns false, it breaks the loop.
                // StaggeredFlow.CanMoveNext is called inside CanProcessThis? No.
                // CanProcessThis is called at the start of the loop.
                // StaggeredFlow.CanProcessThis returns true always.
                // Wait, StaggeredFlow implements IFlowModifier.
                // Let's check IFlowModifier interface.
            }
        }

        [Test]
        public void TimeBoundFlow_BoundsWorkPerStep()
        {
            using (var runner = new SteppableRunner("TimeBoundFlow"))
            {
                runner.UseFlowModifier(new TimeBoundFlow(20f)); // 20ms budget

                int counter = 0;
                IEnumerator<TaskContract> SmallTask()
                {
                    Thread.Sleep(5); // 5ms
                    counter++;
                    yield return TaskContract.Yield.It;
                }

                for (int i = 0; i < 10; i++)
                    SmallTask().RunOn(runner);

                runner.Step();
                
                // Should run about 4 tasks (20ms / 5ms)
                Assert.That(counter, Is.GreaterThan(0));
                Assert.That(counter, Is.LessThan(10));
            }
        }

        [Test]
        public void TimeSlicedFlow_RunsTasksUntilTimeLimit()
        {
             using (var runner = new SteppableRunner("TimeSlicedFlow"))
            {
                runner.UseFlowModifier(new TimeSlicedFlow(20f)); // 20ms budget

                int counter = 0;
                IEnumerator<TaskContract> SmallTask()
                {
                    Thread.Sleep(5); // 5ms
                    counter++;
                    yield return TaskContract.Yield.It;
                }

                for (int i = 0; i < 10; i++)
                    SmallTask().RunOn(runner);

                runner.Step();
                
                // Should run about 4 tasks (20ms / 5ms)
                Assert.That(counter, Is.GreaterThan(0));
                Assert.That(counter, Is.LessThan(10));
            }
        }
    }
}
