using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AiNative.Core;
using AiNative.Physics;

namespace AiNative.PhysicsSandbox
{
    public abstract class SandboxCommand
    {
    }
    public sealed class SpawnBody : SandboxCommand
    {
        public SpawnBody(ShapeKind shape, Vector3 size, Vector3 position)
        {
            // Reuse the physics definition validator; the real ID is assigned when executed.
            var definition = new BodyDefinition(1, shape, true, size, position, Quaternion.Identity);
            Shape = definition.Shape;
            Size = definition.Size;
            Position = definition.Position;
        }
        public ShapeKind Shape { get; }
        public Vector3 Size { get; }
        public Vector3 Position { get; }
    }
    public sealed class RemoveBody : SandboxCommand
    {
        public RemoveBody(ulong id)
        {
            Id = id;
        }
        public ulong Id { get; }
    }
    public sealed class ApplyImpulse : SandboxCommand
    {
        public ApplyImpulse(ulong id, Vector3 impulse)
        {
            if (!BodyDefinition.Finite(impulse))
            {
                throw new ArgumentException("无效冲量。");
            }
            Id = id;
            Impulse = impulse;
        }
        public ulong Id { get; }
        public Vector3 Impulse { get; }
    }
    public sealed class BodyChanged : GameEvent
    {
        public BodyChanged(ulong id, bool spawned)
        {
            Id = id;
            Spawned = spawned;
        }
        public ulong Id { get; }
        public bool Spawned { get; }
    }
    public sealed class ContactChanged : GameEvent
    {
        public ContactChanged(PhysicsContact contact)
        {
            Contact = contact;
        }
        public PhysicsContact Contact { get; }
    }
    public sealed class SandboxState
    {
        public SandboxState(ulong nextId, PhysicsSnapshot physics, IReadOnlyList<BodyState> bodies)
        {
            if (nextId == 0 || bodies.Any(body => body.Id == 0 || body.Id >= nextId)
                || bodies.Select(body => body.Id).Distinct().Count() != bodies.Count)
            {
                throw new ArgumentException("无效实体 ID 状态。");
            }
            NextId = nextId;
            Physics = physics ?? throw new ArgumentNullException(nameof(physics));
            Bodies = Array.AsReadOnly(bodies.OrderBy(body => body.Id).ToArray());
        }
        public ulong NextId { get; }
        public PhysicsSnapshot Physics { get; }
        public IReadOnlyList<BodyState> Bodies { get; }
    }

    public sealed class SandboxSimulation : IGameSimulation<SandboxCommand, IReadOnlyList<ObservedBody>, SandboxState>
    {
        private readonly IPhysicsWorld _world;
        private ulong _nextId;
        private SandboxSimulation(IPhysicsWorld world, ulong nextId)
        {
            _world = world;
            _nextId = nextId;
        }

        public static SandboxSimulation Create(IPhysicsWorldFactory factory, IReadOnlyList<BodyDefinition> definitions, int workerThreads = 0)
        {
            if (definitions.Count > 1024 || definitions.Select(item => item.Id).Distinct().Count() != definitions.Count)
            {
                throw new ArgumentException("刚体数量或标识无效。");
            }
            ulong nextId = checked((definitions.Count == 0 ? 0 : definitions.Max(item => item.Id)) + 1);
            IPhysicsWorld world = factory.Create(workerThreads);
            try
            {
                foreach (BodyDefinition body in definitions.OrderBy(item => item.Id))
                {
                    world.Add(body);
                }
                return new SandboxSimulation(world, nextId);
            }
            catch { world.Dispose(); throw; }
        }

        public static SandboxSimulation Restore(IPhysicsWorldFactory factory, SandboxState state)
        {
            IPhysicsWorld world = factory.Restore(state.Physics);
            try
            {
                IReadOnlyList<BodyState> actual = world.GetBodies();
                if (!actual.Select(body => body.Id).SequenceEqual(state.Bodies.Select(body => body.Id)))
                {
                    throw new ArgumentException("物理对象与诊断状态不一致。");
                }
                return new SandboxSimulation(world, state.NextId);
            }
            catch { world.Dispose(); throw; }
        }

        public CommandResult Execute(SandboxCommand command, DeterministicRandom random)
        {
            IReadOnlyList<BodyState> bodies = _world.GetBodies();
            if (command is SpawnBody spawn)
            {
                if (bodies.Count == 1024 || _nextId == ulong.MaxValue)
                {
                    return CommandResult.Rejected("CapacityExceeded");
                }
                var definition = new BodyDefinition(_nextId, spawn.Shape, true, spawn.Size, spawn.Position, Quaternion.Identity);
                _world.Add(definition);
                return CommandResult.Accepted(new BodyChanged(_nextId++, true));
            }
            if (command is RemoveBody remove)
            {
                if (!bodies.Any(body => body.Id == remove.Id))
                {
                    return CommandResult.Rejected("UnknownBody");
                }
                _world.Remove(remove.Id);
                return CommandResult.Accepted(new BodyChanged(remove.Id, false));
            }
            if (command is ApplyImpulse impulse)
            {
                BodyState? body = bodies.FirstOrDefault(value => value.Id == impulse.Id);
                if (body == null)
                {
                    return CommandResult.Rejected("UnknownBody");
                }
                if (!body.Dynamic)
                {
                    return CommandResult.Rejected("StaticBody");
                }
                _world.ApplyImpulse(impulse.Id, impulse.Impulse);
                return CommandResult.Accepted();
            }
            return CommandResult.Rejected("UnknownCommand");
        }

        public IReadOnlyList<GameEvent> Advance(StepContext context)
        {
            // The simulation owns this order: input effects -> physics -> sorted contact events.
            return Array.AsReadOnly(_world.Step(context.Timing.DeltaSeconds)
                .Select(contact => (GameEvent)new ContactChanged(contact)).ToArray());
        }
        public IReadOnlyList<ObservedBody> GetState() => _world.GetBodies();
        public SandboxState ExportState() => new SandboxState(_nextId, _world.Capture(), _world.GetBodies());
        public void Dispose() => _world.Dispose();
    }
}
