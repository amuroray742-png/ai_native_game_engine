#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using AiNative.UnityCore.TimeMachine;
using NUnit.Framework;
using Clock = AiNative.UnityCore.TimeMachine.TimeMachine;

namespace AiNative.UnityCore.Tests
{
    public sealed class TimeMachineTests
    {
        [Test]
        public void FixedRemovalPreservesLateRegistration()
        {
            var clock = new Clock();
            int calls = 0;
            Action<float> callback = _ => calls++;
            clock.RegisterFixedUpdate(callback, "fixed");
            clock.RegisterLateUpdate(callback, "late");
            clock.RemoveFixedUpdate(callback);
            clock.OnFixedUpdate(1);
            Assert.AreEqual(0, calls);
            clock.OnLateUpdate(1);
            Assert.AreEqual(1, calls);
        }

        [Test]
        public void TicketOwnsItsClockAndCannotRemoveReplacement()
        {
            var first = new Clock();
            var second = new Clock();
            int calls = 0;
            Action<float> callback = _ => calls++;
            ITimeTicket old = first.RegisterUpdate(callback, "first");
            ITimeTicket independent = second.RegisterUpdate(callback, "second");
            int oldId = old.Id!.Value;
            old.Dispose();
            ITimeTicket replacement = first.RegisterUpdate(callback, "replacement");
            old.Dispose();
            first.RemoveUpdate(oldId);
            Assert.IsNull(old.Id);
            Assert.NotNull(replacement.Id);
            Assert.NotNull(independent.Id);
            first.OnUpdate(1);
            second.OnUpdate(1);
            Assert.AreEqual(2, calls);
            replacement.Dispose();
            independent.Dispose();
            first.OnUpdate(1);
            second.OnUpdate(1);
            Assert.AreEqual(2, calls);
        }

        [Test]
        public void RepeatedRegistrationReplacesTheSubscription()
        {
            var clock = new Clock();
            int calls = 0;
            Action<float> callback = _ => calls++;
            ITimeTicket old = clock.RegisterUpdate(callback, "old");
            ITimeTicket current = clock.RegisterUpdate(callback, "new", 4);
            Assert.IsNull(old.Id);
            old.Dispose();
            clock.OnUpdate(1);
            Assert.AreEqual(1, calls);
            Assert.NotNull(current.Id);
        }

        [TestCase(TicketType.Update)]
        [TestCase(TicketType.LateUpdate)]
        [TestCase(TicketType.FixedUpdate)]
        [TestCase(TicketType.OmegaLateUpdate)]
        [TestCase(TicketType.Timer)]
        public void CallbackMutationUsesAStableBatch(TicketType type)
        {
            var clock = new Clock();
            var trace = new List<string>();
            ITimeTicket? first = null;
            ITimeTicket? removed = null;
            first = Register(clock, type, () =>
            {
                trace.Add("first");
                first!.Dispose();
                removed!.Dispose();
                Register(clock, type, () => trace.Add("next"), 100);
            }, 10);
            removed = Register(clock, type, () => trace.Add("removed"), 0);
            Register(clock, type, () => trace.Add("last"), -10);
            Dispatch(clock, type);
            CollectionAssert.AreEqual(new[] { "first", "last" }, trace);
            trace.Clear();
            Dispatch(clock, type);
            CollectionAssert.AreEqual(new[] { "next", "last" }, trace);
        }

        [TestCase(TicketType.Update)]
        [TestCase(TicketType.LateUpdate)]
        [TestCase(TicketType.FixedUpdate)]
        [TestCase(TicketType.OmegaLateUpdate)]
        [TestCase(TicketType.Timer)]
        public void ClearInsideCallbackInvalidatesTheRestOfTheBatch(TicketType type)
        {
            var clock = new Clock();
            int calls = 0;
            Register(clock, type, () => { calls++; clock.ClearAll(); }, 1);
            ITimeTicket old = Register(clock, type, () => calls++, 0);
            Dispatch(clock, type);
            Assert.AreEqual(1, calls);
            Assert.IsNull(old.Id);
            Register(clock, type, () => calls++, 0);
            old.Dispose();
            Dispatch(clock, type);
            Assert.AreEqual(2, calls);
        }

        [Test]
        public void ClearAllRemovesActiveAndNotYetDispatchedRegistrations()
        {
            var clock = new Clock();
            int calls = 0;
            var tickets = new List<ITimeTicket>();
            foreach (TicketType type in Enum.GetValues(typeof(TicketType)))
            {
                tickets.Add(Register(clock, type, () => calls++, 0));
            }
            clock.ClearAll();
            foreach (TicketType type in Enum.GetValues(typeof(TicketType))) { Dispatch(clock, type); }
            Assert.AreEqual(0, calls);
            foreach (ITimeTicket ticket in tickets) { Assert.IsNull(ticket.Id); }
        }

        [Test]
        public void TimerOrderReplacementDoesNotLeaveARecycledEntry()
        {
            var clock = new Clock();
            var trace = new List<string>();
            Action<int> callback = _ => trace.Add("timer");
            ITimeTicket old = clock.RegisterTimer(callback, 1, "old", 1, -2);
            clock.OnTimerUpdate(0);
            ITimeTicket replacement = clock.RegisterTimer(callback, 1, "new", 1, 2);
            clock.RegisterTimer(_ => trace.Add("middle"), 1, "middle", 1, 0);
            old.Dispose();
            clock.OnTimerUpdate(1);
            CollectionAssert.AreEqual(new[] { "timer", "middle" }, trace);
            Assert.IsNull(replacement.Id);
            for (int i = 0; i < 3; i++)
            {
                clock.RegisterTimer(callback, 1, "again", 1, i);
                clock.OnTimerUpdate(1);
            }
            Assert.AreEqual(5, trace.Count);
        }

        [Test]
        public void TimerReplacesItselfWithoutDeletingItsSuccessor()
        {
            var clock = new Clock();
            int calls = 0;
            Action<int>? callback = null;
            callback = _ =>
            {
                calls++;
                if (calls == 1) { clock.RegisterTimer(callback!, 1, "next", 1, 9); }
            };
            clock.RegisterTimer(callback, 1, "first", 1);
            clock.OnTimerUpdate(10);
            Assert.AreEqual(1, calls);
            clock.OnTimerUpdate(1);
            Assert.AreEqual(2, calls);
            clock.OnTimerUpdate(1);
            Assert.AreEqual(2, calls);
        }

        [Test]
        public void SameOrderFollowsRegistrationSequence()
        {
            var clock = new Clock();
            var trace = new List<int>();
            clock.RegisterUpdate(_ => trace.Add(1), "one");
            clock.RegisterUpdate(_ => trace.Add(2), "two");
            clock.RegisterUpdate(_ => trace.Add(3), "three");
            clock.OnUpdate(1);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, trace);
        }

        [Test]
        public void FailedCallbackDoesNotLeaveTheDispatcherReentrantOrTimerAlive()
        {
            var clock = new Clock();
            ITimeTicket timer = clock.RegisterTimer(_ => throw new InvalidOperationException("probe"), 1, "throw", 1);
            Assert.Throws<InvalidOperationException>(() => clock.OnTimerUpdate(1));
            Assert.IsNull(timer.Id);
            Assert.DoesNotThrow(() => clock.OnTimerUpdate(1));
        }

        [Test]
        public void FrameBudgetRestartsWithoutFixedUpdate()
        {
            var clock = new Clock();
            clock.StartFrameLogicTimer();
            clock.SetFrameStartTime();
            Thread.Sleep(15);
            long first = clock.CurrentFrameElapsedTimeTicks;
            clock.FrameTimingEnd();
            clock.SetFrameStartTime();
            Assert.Less(clock.CurrentFrameElapsedTimeTicks, first);
            clock.ClearAll();
            Assert.AreEqual(0, clock.CurrentFrameElapsedTimeTicks);
        }

        private static ITimeTicket Register(Clock clock, TicketType type, Action callback, int order)
        {
            switch (type)
            {
                case TicketType.Update: return clock.RegisterUpdate(_ => callback(), "test", order);
                case TicketType.LateUpdate: return clock.RegisterLateUpdate(_ => callback(), "test", order);
                case TicketType.FixedUpdate: return clock.RegisterFixedUpdate(_ => callback(), "test", order);
                case TicketType.OmegaLateUpdate: return clock.RegisterOmegaLateUpdate(_ => callback(), "test", order);
                default: return clock.RegisterTimer(_ => callback(), 1, "test", order: order);
            }
        }

        private static void Dispatch(Clock clock, TicketType type)
        {
            switch (type)
            {
                case TicketType.Update: clock.OnUpdate(1); break;
                case TicketType.LateUpdate: clock.OnLateUpdate(1); break;
                case TicketType.FixedUpdate: clock.OnFixedUpdate(1); break;
                case TicketType.OmegaLateUpdate: clock.OnOmegaLateUpdate(1); break;
                default: clock.OnTimerUpdate(1); break;
            }
        }
    }
}
