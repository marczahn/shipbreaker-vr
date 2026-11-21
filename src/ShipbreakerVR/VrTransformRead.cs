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
    // thread, 0.6 ms per frame in a quiet bay and 1.2 ms with hundreds of loose parts (see docs/NOTES.md, "The parts'
    // transforms, read on the job threads", also for what this saves). Each frame it reads the position and rotation
    // of every part's Transform one after the other on the main thread (EntityQuery.ToRigidTransformArray), whether
    // the part moved or not, and only then starts the job that writes them to the entities. With the option on, a
    // read-only transform job reads them on all job threads, and the main thread waits for it (and works along): the
    // array is the same, in the same order, when the game's job starts.
    // - The transforms are kept in a TransformAccessArray. Building that anew costs 2 to 3 ms for 1,600 transforms,
    //   several frames' worth of reading, and in a busy bay the system's query changes in many frames, in some
    //   shifts in every one (parts are split, vaporized, taken by the furnaces, or change chunk and with it their
    //   place). So the array is kept in step instead (SlotMap): entities that left the query leave it, new ones join
    //   at its end, and each slot knows where its entity stands in the query's order, which is where the job writes
    //   its result.
    // - When to look: Unity raises the order version of Transform for every entity with a Transform that is added,
    //   removed or moved between chunks, so while it stands still and the count is the same, the query holds the
    //   same entities in the same order. It moves for entities that are none of these parts too, in a busy bay every
    //   frame; then the query's entities are compared with last frame's, and only a difference is acted on.
    // - Each frame a few slots are checked for still holding their entity's Transform (Verified per frame, so every
    //   slot within some seconds), should an entity ever be given another one without changing place.
    // - A Transform that is gone gives the identity, as in the game's own read.
    // - Should anything throw, the option is off until the next start and the game's read is used.
    // - In test mode the result is compared with the game's own read every CheckEvery frames, and every LogEvery
    //   frames the log says how often the query changed, what looking and reading took and whether the checks found
    //   a difference ("Transform read").
    internal static class VrTransformRead
    {
        static VrTransformRead() => VrSession.OnUnload("transform read", Release);

        private const int Batch = 32;
        private const int Verified = 8;
        private const int CheckEvery = 120;
        private const int LogEvery = 600;
        private static TransformAccessArray transforms;
        private static NativeArray<int> target;
        private static readonly SlotMap<Entity> Slots = new SlotMap<Entity>();
        // The query's entities in its order, as of the last look.
        private static Entity[] order = new Entity[0];
        private static int orderCount;
        private static World world;
        private static EntityManager manager;
        private static int orderVersion;
        private static int verifyNext;
        private static int replaced;
        private static bool failed;
        // For the test mode's log line.
        private static int reads, changed, compared, added, removed, checks, differing;
        private static long lookTicks, readTicks;

        private struct ReadJob : IJobParallelForTransform
        {
            [ReadOnly] public NativeArray<int> Target;
            [NativeDisableParallelForRestriction] public NativeArray<RigidTransform> Result;

            public void Execute(int index, TransformAccess transform)
            {
                var at = Target[index];
                if ((uint)at < (uint)Result.Length) Result[at] = math.RigidTransform(transform.rotation, transform.position);
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
                if (!ReferenceEquals(world, system.World)) Forget(system);
                var version = manager.GetComponentOrderVersion<Transform>();
                var count = query.CalculateEntityCount();
                if (version != orderVersion || count != orderCount) Look(query);
                orderVersion = version;
                Verify();
                var read = test ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                // Cleared: a job does not run for a Transform that is gone, and zeros turn and move nothing, like the
                // identity the game writes there.
                result = new NativeArray<RigidTransform>(orderCount, allocator);
                if (transforms.length > 0) new ReadJob { Target = target, Result = result }.ScheduleReadOnly(transforms, Batch).Complete();
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

        // Another world (a hot reload, or the game made its world anew): everything from the start.
        private static void Forget(ComponentSystemBase system)
        {
            Release();
            world = system.World;
            manager = system.EntityManager;
            transforms = new TransformAccessArray(1024);
            Slots.Clear();
            orderCount = 0;
            verifyNext = 0;
        }

        // The query's entities against the last look's; on a difference the transforms are brought in step.
        private static void Look(EntityQuery query)
        {
            compared++;
            var now = query.ToEntityArray(Allocator.Temp);
            var same = now.Length == orderCount;
            if (order.Length < now.Length) order = new Entity[Mathf.NextPowerOfTwo(now.Length)];
            for (var i = 0; i < now.Length; i++)
            {
                var entity = now[i];
                same &= order[i] == entity;
                order[i] = entity;
            }
            orderCount = now.Length;
            now.Dispose();
            // Also while an entity's Transform could not be taken yet.
            if (same && Slots.Count == orderCount) return;
            changed++;
            Slots.Sync(order, orderCount, AddTransform, RemoveTransform);
            if (!target.IsCreated || target.Length < Slots.Target.Length)
            {
                if (target.IsCreated) target.Dispose();
                target = new NativeArray<int>(Slots.Target.Length, Allocator.Persistent);
            }
            NativeArray<int>.Copy(Slots.Target, target, Slots.Count);
        }

        private static readonly Func<Entity, bool> AddTransform = Add;
        private static readonly Action<int> RemoveTransform = Remove;

        private static bool Add(Entity entity)
        {
            var transform = manager.GetComponentObject<Transform>(entity);
            if (transform == null) return false;
            transforms.Add(transform);
            added++;
            return true;
        }

        private static void Remove(int slot)
        {
            transforms.RemoveAtSwapBack(slot);
            removed++;
        }

        private static void Verify()
        {
            for (var n = 0; n < Verified && Slots.Count > 0; n++)
            {
                if (verifyNext >= Slots.Count) verifyNext = 0;
                var transform = manager.GetComponentObject<Transform>(Slots.KeyAt(verifyNext));
                if (transform != null && !ReferenceEquals(transforms[verifyNext], transform))
                {
                    transforms[verifyNext] = transform;
                    if (replaced++ < 5) Plugin.Log.LogInfo($"Transform read: '{transform.name}' is another Transform than before, taken");
                }
                verifyNext++;
            }
        }

        private static void Log(EntityQuery query, NativeArray<RigidTransform> result, long start, long read)
        {
            var end = System.Diagnostics.Stopwatch.GetTimestamp();
            lookTicks += read - start;
            readTicks += end - read;
            if (Time.frameCount % CheckEvery == 0) Check(query, result);
            if (++reads < LogEvery) return;
            var ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency / reads;
            Plugin.Log.LogInfo($"Transform read: {transforms.length} transforms for {result.Length} entities on the job threads, over {reads} frames the query was compared in {compared} and had changed in {changed} " +
                $"({added} transforms added, {removed} removed), per frame {lookTicks * ms:0.000} ms looking and {readTicks * ms:0.000} ms reading; {checks} checks against the game's read, {differing} with differences");
            reads = changed = compared = added = removed = checks = differing = 0;
            lookTicks = readTicks = 0;
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
            if (target.IsCreated) target.Dispose();
            world = null;
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
