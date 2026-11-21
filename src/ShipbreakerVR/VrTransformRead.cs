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
    // - The transforms are kept in a TransformAccessArray, in the order of the system's query, and collected again when
    //   an entity with a Transform was added, removed or moved to another chunk (the order version of Transform, which
    //   Unity raises for every type of an archetype on each of those) or when the query's count differs, and every
    //   Recollect frames in any case, should an entity ever be given another Transform without either.
    // - A Transform that is gone gives the identity, as in the game's own read.
    // - Should anything throw, the option is off until the next start and the game's read is used.
    // - In test mode the result is compared with the game's own read every CheckEvery frames, and the log says how
    //   many transforms differ ("Transform read check").
    internal static class VrTransformRead
    {
        static VrTransformRead() => VrSession.OnUnload("transform read", Release);

        private const int Batch = 32;
        private const int Recollect = 30;
        private const int CheckEvery = 300;
        private static TransformAccessArray transforms;
        private static int orderVersion;
        private static int collectedFrame;
        private static int checks;
        private static bool failed;
        // For the log: how often the transforms were read and how often collected again.
        private static int reads, collected, logged;

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
                var version = system.EntityManager.GetComponentOrderVersion<Transform>();
                var count = query.CalculateEntityCount();
                if (!transforms.isCreated || version != orderVersion || transforms.length != count || Time.frameCount - collectedFrame >= Recollect)
                {
                    var all = query.ToComponentArray<Transform>();
                    if (transforms.isCreated) transforms.SetTransforms(all);
                    else transforms = new TransformAccessArray(all);
                    orderVersion = version;
                    collectedFrame = Time.frameCount;
                    collected++;
                }
                // Cleared: a job does not run for a Transform that is gone, and zeros turn and move nothing, like the
                // identity the game writes there.
                result = new NativeArray<RigidTransform>(transforms.length, allocator);
                if (result.Length > 0) new ReadJob { Result = result }.ScheduleReadOnly(transforms, Batch).Complete();
                if (VrSettings.TestMode.Value && checks < 40 && Time.frameCount % CheckEvery == 0) Check(query, result);
                if (++reads >= 3600 && logged < 3)
                {
                    logged++;
                    Plugin.Log.LogInfo($"Transform read: {result.Length} transforms per frame on the job threads, collected again in {collected} of {reads} frames");
                    reads = collected = 0;
                }
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
            if (differ == 0) Plugin.Log.LogInfo($"Transform read check: {result.Length} transforms, all as the game reads them");
            else Plugin.Log.LogError($"Transform read check: {(differ < 0 ? $"{result.Length} transforms where the game reads {theirs}" : $"{differ} of {result.Length} transforms differ, the furthest by {furthest:0.000} m")}");
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
