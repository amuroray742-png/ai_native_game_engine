using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using AiNative.Core;
using AiNative.Hosting;
using AiNative.Physics;

namespace AiNative.PhysicsSandbox.Hosting
{
    internal static class SandboxData
    {
        internal static IReadOnlyList<BodyDefinition> ReadConfiguration(JsonElement value)
        {
            ProtocolJson.Object(value, "$.configuration", "bodies", "workerThreads");
            ReadWorkerThreads(value);
            var bodies = new List<BodyDefinition>();
            foreach (JsonElement body in ProtocolJson.Array(value.GetProperty("bodies"), "$.configuration.bodies"))
            {
                ProtocolJson.Object(body, "$.body", "id", "shape", "dynamic", "size", "position", "rotation", "mass", "friction", "restitution");
                bodies.Add(new BodyDefinition(ProtocolJson.UInt64(body.GetProperty("id"), "$.body.id"), Shape(body.GetProperty("shape")),
                    Boolean(body.GetProperty("dynamic")), Vector(body.GetProperty("size")), Vector(body.GetProperty("position")),
                    Rotation(body.GetProperty("rotation")), Number(body.GetProperty("mass")), Number(body.GetProperty("friction")), Number(body.GetProperty("restitution"))));
            }
            JsonData.Require(bodies.Count <= 1024 && bodies.Select(body => body.Id).Distinct().Count() == bodies.Count
                && bodies.All(body => body.Id < ulong.MaxValue), "InvalidConfiguration", "$.configuration.bodies");
            return Array.AsReadOnly(bodies.OrderBy(body => body.Id).ToArray());
        }

        internal static int ReadWorkerThreads(JsonElement value)
        {
            int workers = ProtocolJson.Int32(value.GetProperty("workerThreads"), "$.configuration.workerThreads");
            JsonData.Require(workers >= 0 && workers <= 16, "InvalidConfiguration", "$.configuration.workerThreads");
            return workers;
        }

        internal static void WriteConfiguration(Utf8JsonWriter writer, IReadOnlyList<BodyDefinition> bodies, int workerThreads)
        {
            writer.WriteStartObject();
            writer.WriteNumber("workerThreads", workerThreads);
            writer.WriteStartArray("bodies");
            foreach (BodyDefinition body in bodies)
            {
                writer.WriteStartObject();
                writer.WriteString("id", Text(body.Id));
                writer.WriteString("shape", body.Shape.ToString());
                writer.WriteBoolean("dynamic", body.Dynamic);
                writer.WritePropertyName("size");
                WriteVector(writer, body.Size);
                writer.WritePropertyName("position");
                WriteVector(writer, body.Position);
                writer.WritePropertyName("rotation");
                WriteRotation(writer, body.Rotation);
                writer.WritePropertyName("mass");
                PhysicsJsonFloat.Write(writer, body.Mass);
                writer.WritePropertyName("friction");
                PhysicsJsonFloat.Write(writer, body.Friction);
                writer.WritePropertyName("restitution");
                PhysicsJsonFloat.Write(writer, body.Restitution);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        internal static SandboxCommand ReadCommand(JsonElement value)
        {
            JsonData.Require(value.ValueKind == JsonValueKind.Object && value.TryGetProperty("type", out _), "InvalidProtocol", "$.command");
            string type = ProtocolJson.String(value.GetProperty("type"), "$.command.type");
            if (type == "Spawn")
            {
                ProtocolJson.Object(value, "$.command", "type", "shape", "size", "position");
                return new SpawnBody(Shape(value.GetProperty("shape")), Vector(value.GetProperty("size")), Vector(value.GetProperty("position")));
            }
            if (type == "Remove")
            {
                ProtocolJson.Object(value, "$.command", "type", "id");
                return new RemoveBody(ProtocolJson.UInt64(value.GetProperty("id"), "$.command.id"));
            }
            JsonData.Require(type == "Impulse", "InvalidProtocol", "$.command.type");
            ProtocolJson.Object(value, "$.command", "type", "id", "impulse");
            return new ApplyImpulse(ProtocolJson.UInt64(value.GetProperty("id"), "$.command.id"), Vector(value.GetProperty("impulse")));
        }

        internal static void WriteCommand(Utf8JsonWriter writer, SandboxCommand command)
        {
            writer.WriteStartObject();
            if (command is SpawnBody spawn)
            {
                writer.WriteString("type", "Spawn");
                writer.WriteString("shape", spawn.Shape.ToString());
                writer.WritePropertyName("size");
                WriteVector(writer, spawn.Size);
                writer.WritePropertyName("position");
                WriteVector(writer, spawn.Position);
            }
            else if (command is RemoveBody remove)
            {
                writer.WriteString("type", "Remove");
                writer.WriteString("id", Text(remove.Id));
            }
            else if (command is ApplyImpulse impulse)
            {
                writer.WriteString("type", "Impulse");
                writer.WriteString("id", Text(impulse.Id));
                writer.WritePropertyName("impulse");
                WriteVector(writer, impulse.Impulse);
            }
            else
            {
                throw new ArgumentException("未知命令。");
            }
            writer.WriteEndObject();
        }

        internal static SandboxState ReadState(JsonElement value, string backend)
        {
            ProtocolJson.Object(value, "$.state", "nextId", "physics", "bodies");
            JsonElement physics = value.GetProperty("physics");
            ProtocolJson.Object(physics, "$.state.physics", "backend", "data", "hash", "workerThreads");
            JsonData.Require(ProtocolJson.String(physics.GetProperty("backend"), "$.physics.backend") == backend, "PhysicsBackendMismatch", "$.state.physics.backend");
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(ProtocolJson.String(physics.GetProperty("data"), "$.physics.data"));
            }
            catch (FormatException error) { throw new ProtocolException("InvalidState", error.Message, "$.state.physics.data", error); }
            JsonData.Require(JsonData.Hash(bytes) == ProtocolJson.Hash(physics.GetProperty("hash"), "$.physics.hash"), "PhysicsHashMismatch", "$.state.physics.hash");
            var bodies = new List<BodyState>();
            foreach (JsonElement body in ProtocolJson.Array(value.GetProperty("bodies"), "$.state.bodies"))
            {
                ProtocolJson.Object(body, "$.body", "id", "position", "rotation", "velocity", "angularVelocity", "active", "dynamic");
                bodies.Add(new BodyState(ProtocolJson.UInt64(body.GetProperty("id"), "$.body.id"), Vector(body.GetProperty("position")),
                    Rotation(body.GetProperty("rotation")), Vector(body.GetProperty("velocity")), Vector(body.GetProperty("angularVelocity")),
                    Boolean(body.GetProperty("active")), Boolean(body.GetProperty("dynamic"))));
            }
            JsonData.Require(bodies.Count <= 1024 && bodies.Select(body => body.Id).SequenceEqual(bodies.Select(body => body.Id).OrderBy(id => id)),
                "InvalidState", "$.state.bodies");
            return new SandboxState(ProtocolJson.UInt64(value.GetProperty("nextId"), "$.state.nextId"), new PhysicsSnapshot(backend, bytes, ProtocolJson.Int32(physics.GetProperty("workerThreads"), "$.state.physics.workerThreads")), bodies);
        }

        internal static void WriteState(Utf8JsonWriter writer, SandboxState state)
        {
            byte[] bytes = state.Physics.GetData();
            writer.WriteStartObject();
            writer.WriteString("nextId", Text(state.NextId));
            writer.WriteStartObject("physics");
            writer.WriteString("backend", state.Physics.Backend);
            writer.WriteNumber("workerThreads", state.Physics.WorkerThreads);
            writer.WriteBase64String("data", bytes);
            writer.WriteString("hash", JsonData.Hash(bytes));
            writer.WriteEndObject();
            writer.WritePropertyName("bodies");
            WriteBodies(writer, state.Bodies);
            writer.WriteEndObject();
        }

        private static void WriteBodies(Utf8JsonWriter writer, IReadOnlyList<BodyState> bodies)
        {
            writer.WriteStartArray();
            foreach (BodyState body in bodies)
            {
                writer.WriteStartObject();
                WriteBodyFields(writer, body);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        internal static void WriteObservation(Utf8JsonWriter writer, IReadOnlyList<ObservedBody> bodies)
        {
            writer.WriteStartArray();
            foreach (ObservedBody body in bodies)
            {
                writer.WriteStartObject();
                WriteBodyFields(writer, body);
                writer.WriteString("shape", body.Shape.ToString());
                writer.WritePropertyName("size");
                WriteVector(writer, body.Size);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        private static void WriteBodyFields(Utf8JsonWriter writer, BodyState body)
        {
            writer.WriteString("id", Text(body.Id));
            writer.WritePropertyName("position");
            WriteVector(writer, body.Position);
            writer.WritePropertyName("rotation");
            WriteRotation(writer, body.Rotation);
            writer.WritePropertyName("velocity");
            WriteVector(writer, body.Velocity);
            writer.WritePropertyName("angularVelocity");
            WriteVector(writer, body.AngularVelocity);
            writer.WriteBoolean("active", body.Active);
            writer.WriteBoolean("dynamic", body.Dynamic);
        }

        internal static void WriteEvent(Utf8JsonWriter writer, GameEvent value)
        {
            writer.WriteStartObject();
            if (value is BodyChanged body)
            {
                writer.WriteString("type", body.Spawned ? "BodySpawned" : "BodyRemoved");
                writer.WriteString("id", Text(body.Id));
            }
            else if (value is ContactChanged contact)
            {
                writer.WriteString("type", contact.Contact.Started ? "ContactStarted" : "ContactEnded");
                writer.WriteString("first", Text(contact.Contact.First));
                writer.WriteString("second", Text(contact.Contact.Second));
                writer.WriteString("firstShape", Text(contact.Contact.FirstShape));
                writer.WriteString("secondShape", Text(contact.Contact.SecondShape));
            }
            else
            {
                throw new ArgumentException("未知事件。");
            }
            writer.WriteEndObject();
        }

        internal static void ValidateResult(JsonElement result)
        {
            string[] errors = { "UnknownBody", "StaticBody", "CapacityExceeded", "UnknownCommand" };
            foreach (JsonElement input in result.GetProperty("inputs").EnumerateArray())
            {
                if (!input.GetProperty("accepted").GetBoolean())
                {
                    JsonData.Require(Array.IndexOf(errors, input.GetProperty("error").GetString()) >= 0, "InvalidReplay", "$.error");
                }
            }
            foreach (JsonElement item in ProtocolJson.Array(result.GetProperty("events"), "$.events"))
            {
                JsonData.Require(item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out _), "InvalidReplay", "$.events");
                string type = ProtocolJson.String(item.GetProperty("type"), "$.event.type");
                if (type == "BodySpawned" || type == "BodyRemoved")
                {
                    ProtocolJson.Object(item, "$.event", "type", "id");
                    JsonData.Require(ProtocolJson.UInt64(item.GetProperty("id"), "$.id") > 0, "InvalidReplay", "$.id");
                }
                else
                {
                    JsonData.Require(type == "ContactStarted" || type == "ContactEnded", "InvalidReplay", "$.event.type");
                    ProtocolJson.Object(item, "$.event", "type", "first", "second", "firstShape", "secondShape");
                    ulong first = ProtocolJson.UInt64(item.GetProperty("first"), "$.first"), second = ProtocolJson.UInt64(item.GetProperty("second"), "$.second");
                    JsonData.Require(first > 0 && first < second, "InvalidReplay", "$.contact");
                    JsonData.Require(ProtocolJson.UInt64(item.GetProperty("firstShape"), "$.firstShape") <= uint.MaxValue
                        && ProtocolJson.UInt64(item.GetProperty("secondShape"), "$.secondShape") <= uint.MaxValue, "InvalidReplay", "$.shape");
                }
            }
        }

        private static ShapeKind Shape(JsonElement value)
        {
            string name = ProtocolJson.String(value, "$.shape");
            JsonData.Require(name == "Sphere" || name == "Box", "InvalidProtocol", "$.shape");
            return name == "Sphere" ? ShapeKind.Sphere : ShapeKind.Box;
        }
        private static float Number(JsonElement value)
        {
            JsonData.Require(value.ValueKind == JsonValueKind.Number, "InvalidProtocol", "$.number");
            float number = PhysicsJsonFloat.Read(value);
            JsonData.Require(BodyDefinition.Finite(number), "InvalidProtocol", "$.number");
            return number;
        }
        private static bool Boolean(JsonElement value)
        {
            JsonData.Require(value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False, "InvalidProtocol", "$.boolean");
            return value.GetBoolean();
        }
        private static Vector3 Vector(JsonElement value)
        {
            JsonData.Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 3, "InvalidProtocol", "$.vector");
            return new Vector3(Number(value[0]), Number(value[1]), Number(value[2]));
        }
        private static Quaternion Rotation(JsonElement value)
        {
            JsonData.Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 4, "InvalidProtocol", "$.rotation");
            var rotation = new Quaternion(Number(value[0]), Number(value[1]), Number(value[2]), Number(value[3]));
            JsonData.Require(Math.Abs(rotation.LengthSquared() - 1) <= 0.00001f, "InvalidProtocol", "$.rotation");
            return rotation;
        }
        private static void WriteVector(Utf8JsonWriter writer, Vector3 value)
        {
            writer.WriteStartArray();
            PhysicsJsonFloat.Write(writer, value.X);
            PhysicsJsonFloat.Write(writer, value.Y);
            PhysicsJsonFloat.Write(writer, value.Z);
            writer.WriteEndArray();
        }
        private static void WriteRotation(Utf8JsonWriter writer, Quaternion value)
        {
            writer.WriteStartArray();
            PhysicsJsonFloat.Write(writer, value.X);
            PhysicsJsonFloat.Write(writer, value.Y);
            PhysicsJsonFloat.Write(writer, value.Z);
            PhysicsJsonFloat.Write(writer, value.W);
            writer.WriteEndArray();
        }
        private static string Text(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
