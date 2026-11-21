using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BBI.Unity.Game;
using HarmonyLib;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace ShipbreakerVR
{
    // Rendering/LagenParallelLesen: the game's LynxCopyTransformsSystem is the largest of its 340 systems on the main
    // thread, 0.6 ms per frame in a quiet bay and 1.1 ms with hundreds of loose parts (see docs/NOTES.md, "What loose
    // parts cost"). Each frame it reads the position and rotation of every part's Transform one after the other on the
    // main thread (EntityQuery.ToRigidTransformArray), whether the part moved or not, and only then starts the job that
    // writes them to the entities. With the option on, a read-only transform job reads them on all job threads, and the
    // main thread waits for it (and works along): the array is the same, in the same order, when the game's job starts.
    // - The transforms are kept in a TransformAccessArray, in the order of the system's query. Collecting them again
    //   costs more than a frame's read (about 1 ms for 700 transforms), so it is only done when the query's entities
    //   or their order changed: when Unity's order version of Transform moved (it is raised for every type of an
    //   archetype whenever an entity is added to, removed from or moved between its chunks, so it also moves for
    //   entities that are none of these parts, every frame in a busy bay) or the count differs, the query's entities
    //   are compared with the ones the transforms were collected for. And every Recollect frames in any case, should
    //   an entity ever be given another Transform without changing place.
    // - A Transform that is gone gives the identity, as in the game's own read.
    // - Should anything throw, the option is off until the next start and the game's read is used.
    // - In test mode the result is compared with the game's own read every CheckEvery frames, and every LogEvery
    //   frames the log says how often the transforms were collected again, what collecting and reading took and
    //   whether the checks found a difference ("Transform read").
    internal static class VrTransformRead
    {
        static VrTransformRead() => VrSession.OnUnload("transform read", Release);

        private const int Batch = 32;
        private const int Recollect = 600;
        private const int CheckEvery = 300;
        private const int LogEvery = 600;
        private static TransformAccessArray transforms;
        private static Entity[] entities = new Entity[0];
        private static int orderVersion;
        private static int collectedFrame;
        private static bool failed;
        // For the test mode's log line.
        private static int reads, collected, compared, checks, differing;
        private static long collectTicks, readTicks;

        private struct ReadJob : IJobParallelForTransform
        {
            [NativeDisableParallelForRestriction] public NativeArray<RigidTransform> Result;

            public void Execute(int index, TransformAccess transform)
            {
                Result[index] = math.RigidTransform(transform.rotation, transform.position);
            }
        }

        // In place of query.ToRigidTransformArray(allocator) in LynxCopyTransformsSystem.OnUpdate.
        public static NativeArray<RigidTransform> Read(EntityQuery query, Allocator allocator, ComponentSystemBase system)
        {
            if (failed || !VrSettings.ParallelTransformRead.Value) return query.ToRigidTransformArray(allocator);
            var result = default(NativeArray<RigidTransform>);
            try
            {
                var test = VrSettings.TestMode.Value;
                var start = test ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                var version = system.EntityManager.GetComponentOrderVersion<Transform>();
                var count = query.CalculateEntityCount();
                if (!transforms.isCreated || transforms.length != count || Time.frameCount - collectedFrame >= Recollect
                    || (version != orderVersion && !SameEntities(query)))
                {
                    var all = query.ToComponentArray<Transform>();
                    if (transforms.isCreated) transforms.SetTransforms(all);
                    else transforms = new TransformAccessArray(all);
                    var now = query.ToEntityArray(Allocator.Temp);
                    if (entities.Length != now.Length) entities = new Entity[now.Length];
                    now.CopyTo(entities);
                    now.Dispose();
                    collectedFrame = Time.frameCount;
                    collected++;
                }
                orderVersion = version;
                var read = test ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                // Cleared: a job does not run for a Transform that is gone, and zeros turn and move nothing, like the
                // identity the game writes there.
                result = new NativeArray<RigidTransform>(transforms.length, allocator);
                if (result.Length > 0) new ReadJob { Result = result }.ScheduleReadOnly(transforms, Batch).Complete();
                if (test) Log(query, result, start, read);
                return result;
            }
            catch (Exception e)
            {
                failed = true;
                Plugin.Log.LogError($"Transform read: back to the game's own read: {e}");
                if (result.IsCreated) result.Dispose();
                return query.ToRigidTransformArray(allocator);
            }
        }

        // Whether the query still holds the entities the transforms were collected for, in the same order.
        private static bool SameEntities(EntityQuery query)
        {
            compared++;
            var now = query.ToEntityArray(Allocator.Temp);
            var same = now.Length == entities.Length;
            for (var i = 0; same && i < now.Length; i++) same = now[i] == entities[i];
            now.Dispose();
            return same;
        }

        private static void Log(EntityQuery query, NativeArray<RigidTransform> result, long start, long read)
        {
            var end = System.Diagnostics.Stopwatch.GetTimestamp();
            collectTicks += read - start;
            readTicks += end - read;
            if (Time.frameCount % CheckEvery == 0) Check(query, result);
            if (++reads < LogEvery) return;
            var ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency / reads;
            Plugin.Log.LogInfo($"Transform read: {result.Length} transforms on the job threads, over {reads} frames collected again in {collected} and compared in {compared}, " +
                $"per frame {collectTicks * ms:0.000} ms looking and collecting and {readTicks * ms:0.000} ms reading; {checks} checks against the game's read, {differing} with differences");
            reads = collected = compared = checks = differing = 0;
            collectTicks = readTicks = 0;
        }

        private static void Check(EntityQuery query, NativeArray<RigidTransform> result)
        {
            checks++;
            var own = query.ToRigidTransformArray(Allocator.Temp);
            var differ = own.Length == result.Length ? 0 : -1;
            var furthest = 0f;
            for (var i = 0; differ >= 0 && i < own.Length; i++)
            {
                var off = math.distance(own[i].pos, result[i].pos);
                // The same turn can come as q and as -q.
                var turned = 1f - math.abs(math.dot(own[i].rot.value, result[i].rot.value));
                // Zeros stand for the identity where a Transform is gone.
                if (math.lengthsq(result[i].rot.value) == 0f) turned = 1f - math.abs(own[i].rot.value.w);
                if (off > 1e-5f || turned > 1e-5f) differ++;
                furthest = math.max(furthest, off);
            }
            var theirs = own.Length;
            own.Dispose();
            if (differ == 0) return;
            if (differing++ < 5) Plugin.Log.LogError($"Transform read check: {(differ < 0 ? $"{result.Length} transforms where the game reads {theirs}" : $"{differ} of {result.Length} transforms differ, the furthest by {furthest:0.000} m")}");
        }

        private static void Release()
        {
            if (transforms.isCreated) transforms.Dispose();
        }
    }

    [HarmonyPatch(typeof(LynxCopyTransformsSystem), "OnUpdate")]
    internal static class TransformReadPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var target = AccessTools.Method(typeof(LynxEntityQueryExtensionsForTransformStashArray), nameof(LynxEntityQueryExtensionsForTransformStashArray.ToRigidTransformArray));
            var replacement = AccessTools.Method(typeof(VrTransformRead), nameof(VrTransformRead.Read));
            var replaced = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(target))
                {
                    // The system itself as a third argument, for its entity manager.
                    var self = new CodeInstruction(OpCodes.Ldarg_0);
                    self.labels.AddRange(instruction.labels);
                    instruction.labels.Clear();
                    yield return self;
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                    replaced++;
                }
                yield return instruction;
            }
            PatchReport.Count("Transform read", original, replaced, "read(s) of the parts' transforms");
        }
    }
}
