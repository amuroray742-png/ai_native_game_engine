#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using AiNative.Core;
using AiNative.GridGame;
using AiNative.Hosting;
using UnityEditor;
using UnityEngine;

namespace AiNative.Examples.UnityEditor
{
    // Batch entry point for scripts/unity_grid.py. Errors must produce a non-zero Editor exit.
    public static class GridEditorAcceptance
    {
        public static void Run()
        {
            try
            {
                string output = Environment.GetEnvironmentVariable("AINATIVE_GRID_EVIDENCE")
                    ?? throw new ArgumentException("Missing AINATIVE_GRID_EVIDENCE");
                Record(output, "fast", false);
                Record(output, "slow", true);
                VerifyClicks();
                Debug.Log("GridGame Editor acceptance passed.");
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
        }

        private static void Record(string output, string speed, bool slow)
        {
            using var session = new GridEditorSession(Path.Combine(output, "grid.scenario.json"), GridEditorSession.ExampleSeed);
            var commands = CommandFile.Read(JsonData.Parse(File.ReadAllBytes(Path.Combine(output, "grid.commands.json"))), session.Module);
            RecordedRun run = HostVerification.Record(session.Module, session.Configuration, session.Game, commands, EnvironmentIdentity(session),
                checkpointCompleted: (checkpoint, accepted, rejected) =>
                {
                    if (!slow && checkpoint.GetProperty("result").GetProperty("kind").GetString() == "tick")
                    {
                        session.Present(session.Game.Tick, 0);
                        CheckPresented(session.Frames.Presented, checkpoint);
                    }
                });
            if (slow)
            {
                Require(session.Frames.PendingCount == session.Game.Tick && session.Frames.Presented.Tick == 0, "slow consumer did not lag");
                foreach (JsonElement checkpoint in run.Replay.GetProperty("checkpoints").EnumerateArray())
                {
                    if (checkpoint.GetProperty("result").GetProperty("kind").GetString() != "tick")
                    {
                        continue;
                    }
                    long tick = StateCodec.Long(checkpoint.GetProperty("runtime"), "tick");
                    double now = tick * 10;
                    session.Present(now, 0.5);
                    Require(session.Active?.Tick == tick && session.Progress == 0, "animation started out of order");
                    session.Present(now + 5, 0.5);
                    CheckPresented(session.Frames.Presented, checkpoint);
                }
            }
            Require(session.Frames.PendingCount == 0 && session.Frames.Presented.State.Winner == Side.A, "missing presented victory");
            Require(run.Accepted == 6 && run.Rejected == 1 && session.Game.Tick == 8, "wrong fixed input outcome");
            Require(session.Game.Observe().GetRawText() == run.Observation.GetRawText(), "presentation changed observation");
            Require(JsonData.Hash(session.Game.CaptureGameState()) == JsonData.Hash(run.Snapshot.GetProperty("state")), "presentation changed state");
            File.WriteAllBytes(Path.Combine(output, $"editor-{speed}.replay.json"), JsonData.CanonicalBytes(run.Replay));
        }

        private static void CheckPresented(Observation<GridGameView> frame, JsonElement checkpoint)
        {
            JsonElement state = checkpoint.GetProperty("state");
            GridGameView view = frame.State;
            Require(frame.Tick == StateCodec.Long(checkpoint.GetProperty("runtime"), "tick"), "wrong presented Tick");
            Require(view.ActiveSide.ToString() == state.GetProperty("activeSide").GetString()
                && view.TurnIndex == StateCodec.Long(state, "turnIndex") && view.ActionPoints == state.GetProperty("actionPoints").GetInt32()
                && view.Winner?.ToString() == state.GetProperty("winner").GetString(), "wrong presented turn or outcome");
            var units = state.GetProperty("units");
            Require(view.Units.Count == units.GetArrayLength(), "wrong presented unit count");
            for (int index = 0; index < view.Units.Count; index++)
            {
                UnitDefinition unit = view.Units[index];
                JsonElement saved = units[index], pos = saved.GetProperty("position");
                Require(unit.Id == saved.GetProperty("id").GetInt32() && unit.Side.ToString() == saved.GetProperty("side").GetString()
                    && unit.Health == saved.GetProperty("health").GetInt32() && unit.Position.X == pos.GetProperty("x").GetInt32()
                    && unit.Position.Y == pos.GetProperty("y").GetInt32(), $"wrong presented unit at Tick {frame.Tick}");
            }
        }

        private static void VerifyClicks()
        {
            using var session = new GridEditorSession(GridEditorSession.ExampleFile("grid.scenario.json"), GridEditorSession.ExampleSeed);
            session.ClickCell(new GridPosition(0, 0));
            session.ClickCell(new GridPosition(1, 0));
            session.Present(0, 1);
            session.Present(0.5, 1);
            var unit = session.Frames.Presented.State.Units.Single(item => item.Id == 1);
            Require(session.DrawPosition(unit, out _) == new Vector2(0.5f, 0), "move interpolation");
            session.Present(1, 1);
            session.ClickCell(new GridPosition(2, 0));
            session.Present(2, 1);
            var target = session.Frames.Presented.State.Units.Single(item => item.Id == 2);
            session.DrawPosition(target, out bool hit);
            Require(hit && session.Active?.Events[0] is UnitDamaged, "attack feedback");
            session.Present(3, 1);
            session.EndTurn();
            session.Present(4, 0);
            Require(session.Frames.Presented.State.ActiveSide == Side.B && session.Game.Tick == 3, "end turn");
            session.ClickCell(new GridPosition(2, 0));
            session.ClickCell(new GridPosition(1, 0));
            session.Present(5, 0);
            session.EndTurn();
            session.Present(6, 0);
            session.ClickCell(new GridPosition(1, 0));
            session.ClickCell(new GridPosition(2, 0));
            session.Present(7, 1);
            var stale = session.Active ?? throw new InvalidOperationException("expected victory animation");
            Require(stale.Observation.State.Winner == Side.A, "clicks did not finish the match");
            session.Dispose();
            Require(!session.Frames.Complete(stale), "closed match accepted an old animation");
        }

        private static HostEnvironment EnvironmentIdentity(GridEditorSession session)
        {
            var assemblies = new List<Assembly>
            {
                typeof(GridEditorAcceptance).Assembly, typeof(EditorWindow).Assembly, typeof(Application).Assembly, typeof(GUI).Assembly,
                typeof(RuntimeState).Assembly, typeof(IGameModule).Assembly, typeof(JsonDocument).Assembly,
                typeof(System.Text.Encodings.Web.JavaScriptEncoder).Assembly, typeof(System.IO.Pipelines.PipeReader).Assembly
            };
            // Resolve the exact strong names requested by this JSON assembly in the Editor's Mono domain.
            foreach (AssemblyName reference in typeof(JsonDocument).Assembly.GetReferencedAssemblies())
            {
                if (reference.Name == "Microsoft.Bcl.AsyncInterfaces" || reference.Name == "System.Runtime.CompilerServices.Unsafe")
                {
                    assemblies.Add(Assembly.Load(reference));
                }
            }
            assemblies.AddRange(session.Module.CodeTypes.Select(type => type.Assembly));
            var components = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (Assembly assembly in assemblies.Distinct())
            {
                components.Add(assembly.GetName().Name, JsonData.Hash(File.ReadAllBytes(assembly.Location)));
            }
            Require(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
                assembly.GetName().Name.StartsWith("AiNative.Physics", StringComparison.Ordinal)), "physics adapter loaded in GridGame");
            return new HostEnvironment("Unity Editor " + Application.unityVersion + " / Mono " + Environment.Version,
                Application.platform.ToString(), RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), JsonData.Write(writer =>
                {
                    writer.WriteStartObject();
                    foreach (var component in components)
                    {
                        writer.WriteString(component.Key, component.Value);
                    }
                    writer.WriteEndObject();
                }));
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
