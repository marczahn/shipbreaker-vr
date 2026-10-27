using Unity.Jobs.LowLevel.Unsafe;

namespace ShipbreakerVR
{
    // A test of whether fewer job worker threads leave the main and render threads more of the processor: Unity starts
    // one worker per logical core but one, so on a four core processor with Hyper-Threading the workers share physical
    // cores with the main thread, the render thread, Enlighten, Wwise and Virtual Desktop. Tasten/JobWorker (F9) steps
    // through Unity's count, 4 and 2; the frame time line is logged per count. Not kept: a restart starts with
    // Unity's own count again.
    internal static class VrJobWorkers
    {
        static VrJobWorkers() => VrSession.OnUnload("job workers", Restore);

        private static readonly int[] Steps = { 4, 2 };
        private static int step = -1;

        public static int Count => JobsUtility.JobWorkerCount;

        public static void Cycle()
        {
            step++;
            if (step >= Steps.Length)
            {
                step = -1;
                JobsUtility.ResetJobWorkerCount();
            }
            else JobsUtility.JobWorkerCount = System.Math.Min(Steps[step], JobsUtility.JobWorkerMaximumCount);
            Plugin.Log.LogInfo($"Job workers: {JobsUtility.JobWorkerCount}{(step < 0 ? " (Unity's own count)" : "")} ({VrSettings.JobWorkerKey.Value})");
        }

        private static void Restore()
        {
            if (step < 0) return;
            JobsUtility.ResetJobWorkerCount();
            step = -1;
        }
    }
}
