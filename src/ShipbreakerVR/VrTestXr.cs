using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR.OpenXR.Features;

namespace ShipbreakerVR
{
    // [Test] Testmodus: the head, the controllers' poses and their buttons can be set by a test script (VrTestScript)
    // instead of the headset, for testing without wearing it. This works between Unity and the OpenXR runtime, as an
    // API layer would: an OpenXR feature of ours (VrTestFeature) hooks the calls Unity makes for poses and input, and
    // overwrites what the runtime returned with the script's values. Unity and the mod see a headset that moves and
    // controllers that are pressed. Whatever the script does not set passes through unchanged.
    //
    // The hooked calls:
    //   xrLocateViews                        the eyes' poses (rendering): the script's head, each eye half the IPD to the side
    //   xrLocateSpace                        the head (view space) and the controllers (action spaces)
    //   xrCreateReferenceSpace/ActionSpace   which space is the head, which a controller's grip or aim pose
    //   xrSuggestInteractionProfileBindings  which action is bound to which input (/user/hand/right/input/trigger/value)
    //   xrGetActionState*                    the buttons, triggers, grips, sticks and whether a pose is active
    // Works with any runtime: with Unity's mock runtime (VrTestRuntime, no headset at all) and with the real one.
    //
    // The feature is created once per game session and lives on in the first loaded assembly after a reload (F6); it
    // hands each call to the current plugin's handler through AppDomain data, as VrAlternateEyesFeature does.
    internal static class VrTestXr
    {
        public const string HandlerKey = "ShipbreakerVR.Test.Handler";
        public const string GetProcKey = "ShipbreakerVR.Test.GetProc";
        public const string InstanceKey = "ShipbreakerVR.Test.Instance";

        // What the feature hands on; a, b, c are the call's arguments as listed at each case of Handle.
        public enum Call { LocateViews, LocateSpace, CreateReferenceSpace, CreateActionSpace, SuggestBindings, StateBoolean, StateFloat, StateVector2, StatePose, SyncActions }

        public static bool Hooked => AppDomain.CurrentDomain.GetData(GetProcKey) is IntPtr p && p != IntPtr.Zero;

        // OpenXR, 64 bit: offsets into the structures the calls fill.
        private const int ViewSize = 64, ViewPoseOffset = 16, ViewStateFlagsOffset = 16;
        private const int LocationFlagsOffset = 16, LocationPoseOffset = 24;
        private const int ReferenceSpaceTypeOffset = 16;
        private const int ActionSpaceActionOffset = 16, ActionSpaceSubactionOffset = 24;
        private const int BindingProfileOffset = 16, BindingCountOffset = 24, BindingListOffset = 32;
        private const int GetInfoActionOffset = 16, GetInfoSubactionOffset = 24;
        private const int StateValueOffset = 16, StateChangedOffset = 20, StateActiveOffset = 32;
        private const int Vector2ChangedOffset = 24, Vector2ActiveOffset = 40, PoseActiveOffset = 16;
        private const ulong AllLocationFlags = 0xF;
        private const int ReferenceSpaceView = 1;

        // The poses the script sets, in Unity's tracking space (meters, Unity's left-handed axes).
        public struct Pose
        {
            public Vector3 Position;
            public Quaternion Rotation;

            public Pose(Vector3 position, Quaternion rotation)
            {
                Position = position;
                Rotation = rotation;
            }
        }

        public enum Device { Head, Left, Right }

        private static readonly object Gate = new object();
        private static readonly Pose?[] Poses = new Pose?[3];
        // Inputs by their path without the hand, e.g. "input/trigger/value"; per hand. A stick has two values.
        private static readonly Dictionary<string, float[]>[] Inputs = { null, new Dictionary<string, float[]>(), new Dictionary<string, float[]>() };
        // The aim pose relative to the grip pose, for a hand whose pose the script sets.
        private static Quaternion aimOffset = Quaternion.identity;
        public static float Ipd = 0.064f;

        // What the runtime reported last, before the script's values: for measuring the real controllers (dump).
        private static readonly Pose?[] Reported = new Pose?[5];

        // Spaces: the head (view space), and per action space its action and hand.
        private static readonly HashSet<ulong> ViewSpaces = new HashSet<ulong>();
        private static readonly Dictionary<ulong, (ulong Action, ulong Subaction)> ActionSpaces = new Dictionary<ulong, (ulong, ulong)>();
        // Per action, the inputs it is bound to (full paths), from the suggested bindings of every profile.
        private static readonly Dictionary<ulong, List<string>> Bindings = new Dictionary<ulong, List<string>>();
        private static readonly Dictionary<ulong, string> PathNames = new Dictionary<ulong, string>();
        private static readonly Dictionary<(ulong, ulong), float> LastSent = new Dictionary<(ulong, ulong), float>();

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int GetInstanceProcAddrFn(ulong instance, [MarshalAs(UnmanagedType.LPStr)] string name, out IntPtr function);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int PathToStringFn(ulong instance, ulong path, uint capacity, out uint count, IntPtr buffer);

        private static PathToStringFn pathToString;
        private static Action<int, IntPtr, IntPtr, IntPtr> handler;
        private static int errors;

        public static bool Active
        {
            get
            {
                lock (Gate) return Poses[0].HasValue || Poses[1].HasValue || Poses[2].HasValue || Inputs[1].Count > 0 || Inputs[2].Count > 0;
            }
        }

        public static void Register()
        {
            if (handler != null) return;
            handler = Handle;
            AppDomain.CurrentDomain.SetData(HandlerKey, handler);
            VrSession.OnUnload("test input", Unregister);
        }

        private static void Unregister()
        {
            if (AppDomain.CurrentDomain.GetData(HandlerKey) as Action<int, IntPtr, IntPtr, IntPtr> == handler) AppDomain.CurrentDomain.SetData(HandlerKey, null);
            handler = null;
        }

        public static void SetPose(Device device, Pose? pose)
        {
            lock (Gate) Poses[(int)device] = pose;
        }

        public static Pose? GetPose(Device device)
        {
            lock (Gate) return Poses[(int)device];
        }

        public static void SetInput(Device hand, string input, params float[] value)
        {
            lock (Gate)
            {
                if (value == null) Inputs[(int)hand].Remove(input);
                else Inputs[(int)hand][input] = value;
            }
        }

        public static void SetAimOffset(Quaternion offset)
        {
            lock (Gate) aimOffset = offset;
        }

        public static void Reset()
        {
            lock (Gate)
            {
                for (var i = 0; i < Poses.Length; i++) Poses[i] = null;
                Inputs[1].Clear();
                Inputs[2].Clear();
            }
        }

        // The last poses from the runtime, before the script's: head, left grip, right grip, left aim, right aim.
        public static string DescribeReported()
        {
            var names = new[] { "head", "left grip", "right grip", "left aim", "right aim" };
            var parts = new List<string>();
            lock (Gate)
            {
                for (var i = 0; i < Reported.Length; i++)
                {
                    if (!Reported[i].HasValue) continue;
                    var p = Reported[i].Value;
                    parts.Add($"{names[i]} at {VrTestScript.Format(p.Position)} rot {VrTestScript.Format(p.Rotation.eulerAngles)}");
                }
                if (Reported[1].HasValue && Reported[3].HasValue)
                    parts.Add($"left aim relative to grip {VrTestScript.Format((Quaternion.Inverse(Reported[1].Value.Rotation) * Reported[3].Value.Rotation).eulerAngles)}");
                if (Reported[2].HasValue && Reported[4].HasValue)
                    parts.Add($"right aim relative to grip {VrTestScript.Format((Quaternion.Inverse(Reported[2].Value.Rotation) * Reported[4].Value.Rotation).eulerAngles)}");
            }
            return parts.Count == 0 ? "nothing reported yet" : string.Join("; ", parts);
        }

        private static void Handle(int call, IntPtr a, IntPtr b, IntPtr c)
        {
            try
            {
                switch ((Call)call)
                {
                    // a: XrViewState*, b: view count, c: XrView*
                    case Call.LocateViews: LocateViews(a, (int)b, c); break;
                    // a: space, b: base space, c: XrSpaceLocation*
                    case Call.LocateSpace: LocateSpace((ulong)a, c); break;
                    // a: XrReferenceSpaceCreateInfo*, b: the new space
                    case Call.CreateReferenceSpace:
                        if (Marshal.ReadInt32(a, ReferenceSpaceTypeOffset) == ReferenceSpaceView) lock (Gate) ViewSpaces.Add((ulong)b);
                        break;
                    // a: XrActionSpaceCreateInfo*, b: the new space
                    case Call.CreateActionSpace:
                        lock (Gate) ActionSpaces[(ulong)b] = ((ulong)Marshal.ReadInt64(a, ActionSpaceActionOffset), (ulong)Marshal.ReadInt64(a, ActionSpaceSubactionOffset));
                        break;
                    // a: instance, b: XrInteractionProfileSuggestedBinding*
                    case Call.SuggestBindings: SuggestBindings((ulong)a, b); break;
                    // a: XrActionStateGetInfo*, b: the state
                    case Call.StateBoolean:
                    case Call.StateFloat:
                    case Call.StateVector2:
                    case Call.StatePose: ActionState((Call)call, a, b); break;
                }
            }
            catch (Exception e)
            {
                if (errors++ < 5) Plugin.Log.LogError($"Test input: {(Call)call} failed: {e}");
            }
        }

        private static void LocateViews(IntPtr viewState, int count, IntPtr views)
        {
            Pose head;
            lock (Gate)
            {
                if (!Poses[0].HasValue) return;
                head = Poses[0].Value;
            }
            if (views == IntPtr.Zero || count < 1 || count > 4) return;
            if (viewState != IntPtr.Zero) Marshal.WriteInt64(viewState, ViewStateFlagsOffset, (long)AllLocationFlags);
            for (var i = 0; i < count; i++)
            {
                var side = count == 1 ? 0f : (i == 0 ? -0.5f : 0.5f) * Ipd;
                var eye = new Pose(head.Position + head.Rotation * new Vector3(side, 0f, 0f), head.Rotation);
                WritePose(views + i * ViewSize + ViewPoseOffset, eye);
            }
        }

        private static void LocateSpace(ulong space, IntPtr location)
        {
            if (location == IntPtr.Zero) return;
            int reported;
            Pose? pose;
            var tracked = (Marshal.ReadInt64(location, LocationFlagsOffset) & 3) == 3;
            lock (Gate)
            {
                if (ViewSpaces.Contains(space))
                {
                    reported = 0;
                    pose = Poses[0];
                }
                else if (ActionSpaces.TryGetValue(space, out var entry) && Hand(entry.Subaction) is Device hand)
                {
                    var aim = IsAim(entry.Action, hand);
                    reported = (int)hand + (aim ? 2 : 0);
                    pose = Poses[(int)hand];
                    if (pose.HasValue && aim) pose = new Pose(pose.Value.Position, pose.Value.Rotation * aimOffset);
                }
                else return;
                if (tracked) Reported[reported] = ReadPose(location + LocationPoseOffset);
            }
            if (!pose.HasValue) return;
            Marshal.WriteInt64(location, LocationFlagsOffset, (long)AllLocationFlags);
            WritePose(location + LocationPoseOffset, pose.Value);
        }

        private static bool IsAim(ulong action, Device hand)
        {
            if (!Bindings.TryGetValue(action, out var paths)) return false;
            var prefix = HandPath(hand);
            foreach (var path in paths)
                if (path.StartsWith(prefix) && path.EndsWith("/aim/pose")) return true;
            return false;
        }

        private static void SuggestBindings(ulong instance, IntPtr suggested)
        {
            var count = Marshal.ReadInt32(suggested, BindingCountOffset);
            var list = Marshal.ReadIntPtr(suggested, BindingListOffset);
            var profile = PathName(instance, (ulong)Marshal.ReadInt64(suggested, BindingProfileOffset));
            lock (Gate)
            {
                for (var i = 0; i < count; i++)
                {
                    var action = (ulong)Marshal.ReadInt64(list, i * 16);
                    var binding = PathName(instance, (ulong)Marshal.ReadInt64(list, i * 16 + 8));
                    if (binding == null) continue;
                    if (!Bindings.TryGetValue(action, out var paths)) Bindings[action] = paths = new List<string>();
                    if (!paths.Contains(binding)) paths.Add(binding);
                }
            }
            Plugin.Log.LogInfo($"Test input: {count} bindings of {profile} recorded");
        }

        private static void ActionState(Call call, IntPtr getInfo, IntPtr state)
        {
            var action = (ulong)Marshal.ReadInt64(getInfo, GetInfoActionOffset);
            var subaction = (ulong)Marshal.ReadInt64(getInfo, GetInfoSubactionOffset);
            float[] value = null;
            lock (Gate)
            {
                if (!Bindings.TryGetValue(action, out var paths)) return;
                var subactionName = subaction == 0 ? null : PathNames.TryGetValue(subaction, out var n) ? n : null;
                foreach (var path in paths)
                {
                    if (subactionName != null && !path.StartsWith(subactionName + "/")) continue;
                    var hand = path.StartsWith(HandPath(Device.Left)) ? Device.Left : path.StartsWith(HandPath(Device.Right)) ? Device.Right : (Device?)null;
                    if (hand == null) continue;
                    var input = path.Substring(HandPath(hand.Value).Length);
                    if (call == Call.StatePose)
                    {
                        if (Poses[(int)hand.Value].HasValue) value = new[] { 1f };
                    }
                    else if (Inputs[(int)hand.Value].TryGetValue(input, out var v)) value = v;
                    if (value != null) break;
                }
            }
            if (value == null) return;
            var key = (action, subaction);
            switch (call)
            {
                case Call.StatePose:
                    Marshal.WriteInt32(state, PoseActiveOffset, 1);
                    return;
                case Call.StateBoolean:
                    var down = value[0] >= 0.5f ? 1 : 0;
                    Marshal.WriteInt32(state, StateValueOffset, down);
                    Marshal.WriteInt32(state, StateChangedOffset, Changed(key, down) ? 1 : 0);
                    Marshal.WriteInt32(state, StateActiveOffset, 1);
                    return;
                case Call.StateFloat:
                    Marshal.WriteInt32(state, StateValueOffset, BitConverter.ToInt32(BitConverter.GetBytes(value[0]), 0));
                    Marshal.WriteInt32(state, StateChangedOffset, Changed(key, value[0]) ? 1 : 0);
                    Marshal.WriteInt32(state, StateActiveOffset, 1);
                    return;
                case Call.StateVector2:
                    var x = value[0];
                    var y = value.Length > 1 ? value[1] : 0f;
                    Marshal.WriteInt32(state, StateValueOffset, BitConverter.ToInt32(BitConverter.GetBytes(x), 0));
                    Marshal.WriteInt32(state, StateValueOffset + 4, BitConverter.ToInt32(BitConverter.GetBytes(y), 0));
                    Marshal.WriteInt32(state, Vector2ChangedOffset, Changed(key, x * 1000f + y) ? 1 : 0);
                    Marshal.WriteInt32(state, Vector2ActiveOffset, 1);
                    return;
            }
        }

        private static bool Changed((ulong, ulong) key, float value)
        {
            lock (Gate)
            {
                var changed = !LastSent.TryGetValue(key, out var last) || last != value;
                LastSent[key] = value;
                return changed;
            }
        }

        private static Device? Hand(ulong subaction)
        {
            if (!PathNames.TryGetValue(subaction, out var name)) return null;
            if (name == HandPath(Device.Left).TrimEnd('/')) return Device.Left;
            if (name == HandPath(Device.Right).TrimEnd('/')) return Device.Right;
            return null;
        }

        private static string HandPath(Device hand) => hand == Device.Left ? "/user/hand/left/" : "/user/hand/right/";

        // A path's string, through the runtime's xrPathToString; kept, so later calls need no lookup (and the render
        // thread none). The hands' paths are known by the time Unity creates its action spaces.
        private static string PathName(ulong instance, ulong path)
        {
            lock (Gate)
                if (PathNames.TryGetValue(path, out var known)) return known;
            if (pathToString == null)
            {
                if (!(AppDomain.CurrentDomain.GetData(GetProcKey) is IntPtr getProcPtr) || getProcPtr == IntPtr.Zero) return null;
                var getProc = Marshal.GetDelegateForFunctionPointer<GetInstanceProcAddrFn>(getProcPtr);
                if (getProc(instance, "xrPathToString", out var fn) != 0 || fn == IntPtr.Zero) return null;
                pathToString = Marshal.GetDelegateForFunctionPointer<PathToStringFn>(fn);
            }
            var buffer = Marshal.AllocHGlobal(256);
            try
            {
                if (pathToString(instance, path, 256, out var count, buffer) != 0 || count == 0) return null;
                var name = Marshal.PtrToStringAnsi(buffer, (int)count - 1);
                lock (Gate)
                {
                    PathNames[path] = name;
                    // The hands as subaction paths: their own XrPath, without "/input/...".
                    foreach (var hand in new[] { Device.Left, Device.Right })
                        if (name.StartsWith(HandPath(hand))) KnowHand(instance, hand);
                }
                return name;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int StringToPathFn(ulong instance, [MarshalAs(UnmanagedType.LPStr)] string name, out ulong path);

        private static StringToPathFn stringToPath;

        private static void KnowHand(ulong instance, Device hand)
        {
            var name = HandPath(hand).TrimEnd('/');
            if (PathNames.ContainsValue(name)) return;
            if (stringToPath == null)
            {
                var getProc = Marshal.GetDelegateForFunctionPointer<GetInstanceProcAddrFn>((IntPtr)AppDomain.CurrentDomain.GetData(GetProcKey));
                if (getProc(instance, "xrStringToPath", out var fn) != 0 || fn == IntPtr.Zero) return;
                stringToPath = Marshal.GetDelegateForFunctionPointer<StringToPathFn>(fn);
            }
            if (stringToPath(instance, name, out var path) == 0) PathNames[path] = name;
        }

        // Unity's axes are OpenXR's with z mirrored.
        private static void WritePose(IntPtr at, Pose pose)
        {
            var q = pose.Rotation;
            var p = pose.Position;
            WriteFloats(at, -q.x, -q.y, q.z, q.w, p.x, p.y, -p.z);
        }

        private static Pose ReadPose(IntPtr at)
        {
            var f = new float[7];
            Marshal.Copy(at, f, 0, 7);
            return new Pose(new Vector3(f[4], f[5], -f[6]), new Quaternion(-f[0], -f[1], f[2], f[3]));
        }

        private static void WriteFloats(IntPtr at, params float[] values) => Marshal.Copy(values, 0, at, values.Length);
    }

    // Added to the OpenXR settings when [Test] Testmodus is on (VrXr.LoadSettings).
    internal class VrTestFeature : OpenXRFeature
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int GetInstanceProcAddrFn(ulong instance, IntPtr name, IntPtr function);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int LocateViewsFn(ulong session, IntPtr locateInfo, IntPtr viewState, uint capacity, IntPtr count, IntPtr views);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int LocateSpaceFn(ulong space, ulong baseSpace, long time, IntPtr location);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int CreateSpaceFn(ulong session, IntPtr createInfo, IntPtr space);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int SuggestBindingsFn(ulong instance, IntPtr suggested);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int ActionStateFn(ulong session, IntPtr getInfo, IntPtr state);

        // Static: the runtime calls them for as long as the instance lives.
        private static GetInstanceProcAddrFn originalGetProc, hookedGetProc;
        private static LocateViewsFn originalLocateViews, hookedLocateViews;
        private static LocateSpaceFn originalLocateSpace, hookedLocateSpace;
        private static CreateSpaceFn originalReferenceSpace, hookedReferenceSpace, originalActionSpace, hookedActionSpace;
        private static SuggestBindingsFn originalSuggest, hookedSuggest;
        private static ActionStateFn originalBoolean, hookedBoolean, originalFloat, hookedFloat, originalVector2, hookedVector2, originalPose, hookedPose;

        protected override IntPtr HookGetInstanceProcAddr(IntPtr func)
        {
            originalGetProc = Marshal.GetDelegateForFunctionPointer<GetInstanceProcAddrFn>(func);
            hookedGetProc = GetInstanceProcAddr;
            AppDomain.CurrentDomain.SetData(VrTestXr.GetProcKey, func);
            Plugin.Log.LogInfo("Test input: OpenXR feature hooked xrGetInstanceProcAddr");
            return Marshal.GetFunctionPointerForDelegate(hookedGetProc);
        }

        protected override bool OnInstanceCreate(ulong xrInstance)
        {
            AppDomain.CurrentDomain.SetData(VrTestXr.InstanceKey, xrInstance);
            return true;
        }

        private static void Hand(VrTestXr.Call call, IntPtr a, IntPtr b, IntPtr c)
        {
            if (AppDomain.CurrentDomain.GetData(VrTestXr.HandlerKey) is Action<int, IntPtr, IntPtr, IntPtr> handler) handler((int)call, a, b, c);
        }

        private static int GetInstanceProcAddr(ulong instance, IntPtr name, IntPtr function)
        {
            var result = originalGetProc(instance, name, function);
            if (result != 0 || function == IntPtr.Zero) return result;
            var original = Marshal.ReadIntPtr(function);
            if (original == IntPtr.Zero) return result;
            Delegate hooked = null;
            switch (Marshal.PtrToStringAnsi(name))
            {
                case "xrLocateViews":
                    originalLocateViews = Marshal.GetDelegateForFunctionPointer<LocateViewsFn>(original);
                    hooked = hookedLocateViews = (session, info, state, capacity, count, views) =>
                    {
                        var r = originalLocateViews(session, info, state, capacity, count, views);
                        if (r == 0 && count != IntPtr.Zero) Hand(VrTestXr.Call.LocateViews, state, (IntPtr)Marshal.ReadInt32(count), views);
                        return r;
                    };
                    break;
                case "xrLocateSpace":
                    originalLocateSpace = Marshal.GetDelegateForFunctionPointer<LocateSpaceFn>(original);
                    hooked = hookedLocateSpace = (space, baseSpace, time, location) =>
                    {
                        var r = originalLocateSpace(space, baseSpace, time, location);
                        if (r == 0) Hand(VrTestXr.Call.LocateSpace, (IntPtr)(long)space, (IntPtr)(long)baseSpace, location);
                        return r;
                    };
                    break;
                case "xrCreateReferenceSpace":
                    originalReferenceSpace = Marshal.GetDelegateForFunctionPointer<CreateSpaceFn>(original);
                    hooked = hookedReferenceSpace = (session, info, space) =>
                    {
                        var r = originalReferenceSpace(session, info, space);
                        if (r == 0 && space != IntPtr.Zero) Hand(VrTestXr.Call.CreateReferenceSpace, info, (IntPtr)Marshal.ReadInt64(space), IntPtr.Zero);
                        return r;
                    };
                    break;
                case "xrCreateActionSpace":
                    originalActionSpace = Marshal.GetDelegateForFunctionPointer<CreateSpaceFn>(original);
                    hooked = hookedActionSpace = (session, info, space) =>
                    {
                        var r = originalActionSpace(session, info, space);
                        if (r == 0 && space != IntPtr.Zero) Hand(VrTestXr.Call.CreateActionSpace, info, (IntPtr)Marshal.ReadInt64(space), IntPtr.Zero);
                        return r;
                    };
                    break;
                case "xrSuggestInteractionProfileBindings":
                    originalSuggest = Marshal.GetDelegateForFunctionPointer<SuggestBindingsFn>(original);
                    hooked = hookedSuggest = (inst, suggested) =>
                    {
                        var r = originalSuggest(inst, suggested);
                        if (r == 0) Hand(VrTestXr.Call.SuggestBindings, (IntPtr)(long)inst, suggested, IntPtr.Zero);
                        return r;
                    };
                    break;
                case "xrGetActionStateBoolean":
                    originalBoolean = Marshal.GetDelegateForFunctionPointer<ActionStateFn>(original);
                    hooked = hookedBoolean = (session, info, state) => State(originalBoolean, VrTestXr.Call.StateBoolean, session, info, state);
                    break;
                case "xrGetActionStateFloat":
                    originalFloat = Marshal.GetDelegateForFunctionPointer<ActionStateFn>(original);
                    hooked = hookedFloat = (session, info, state) => State(originalFloat, VrTestXr.Call.StateFloat, session, info, state);
                    break;
                case "xrGetActionStateVector2f":
                    originalVector2 = Marshal.GetDelegateForFunctionPointer<ActionStateFn>(original);
                    hooked = hookedVector2 = (session, info, state) => State(originalVector2, VrTestXr.Call.StateVector2, session, info, state);
                    break;
                case "xrGetActionStatePose":
                    originalPose = Marshal.GetDelegateForFunctionPointer<ActionStateFn>(original);
                    hooked = hookedPose = (session, info, state) => State(originalPose, VrTestXr.Call.StatePose, session, info, state);
                    break;
            }
            if (hooked != null) Marshal.WriteIntPtr(function, Marshal.GetFunctionPointerForDelegate(hooked));
            return result;
        }

        private static int State(ActionStateFn original, VrTestXr.Call call, ulong session, IntPtr info, IntPtr state)
        {
            var r = original(session, info, state);
            if (r == 0) Hand(call, info, state, IntPtr.Zero);
            return r;
        }
    }
}
