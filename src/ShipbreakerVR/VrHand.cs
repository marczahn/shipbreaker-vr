using BBI.Unity.Game;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // What each hand is doing, in one place. Before, every module kept its own flags (the tool hand in VrToolInput, the
    // second tool's hand and the hand the scanner hid a tool in in VrDualTools, holding on in VrClimb), and questions like
    // "is this hand free?" combined five of them in different places.
    //   Empty       nothing in the hand: its grip draws a tool at the belt or holds on elsewhere
    //   GameTool    the game's equipped tool (EquipmentController.CurrentEquipment), aimed and worked with this hand
    //   SecondTool  the second tool next to the game's one (VrDualTools)
    //   HiddenTool  a tool the scanner in the other hand hid; it comes back while the grip stays held (VrDualTools)
    //   Holding     holding on to something (VrClimb)
    // The modules change it only through the transitions here: VrToolInput (drawing, putting away, taking over),
    // VrDualTools (the second and the hidden tool) and VrClimb (holding on, letting go). A hand gets a tool only while it
    // does not hold on, and holds on only while it is free (Free), so one hand never does two of these at once. The game's
    // tool goes into the hand only while the tools aim with the hand; without that the tools work from the head as in the
    // flat game and both hands are empty (or holding on). VrToolInput.Update brings the state up to date with the game
    // once per frame (VrFrame), before anything reads it; each transition updates it right away.
    internal enum HandState { Empty, GameTool, SecondTool, HiddenTool, Holding }

    internal sealed class VrHand
    {
        public static readonly VrHand Left = new VrHand(VrInput.Left);
        public static readonly VrHand Right = new VrHand(VrInput.Right);
        public static readonly VrHand[] Both = { Left, Right };

        public static VrHand Of(XRNode node) => node == XRNode.LeftHand ? Left : Right;

        // The hand that works the game's tool: the right one unless the left one drew it or took it over; back to the
        // right one once the tool is put away. Kept while the tools do not aim with the hand (tracking lost for a moment),
        // so the tool comes back into the same hand.
        public static VrHand GameToolHand { get; private set; } = Right;

        // The hand that holds the second tool (VrDualTools), or null.
        public static VrHand SecondHand => Left.second != EquipmentController.Equipment.None ? Left
            : Right.second != EquipmentController.Equipment.None ? Right : null;

        // The hand the scanner hid a tool in, or null.
        public static VrHand HiddenHand => Left.hidden != EquipmentController.Equipment.None ? Left
            : Right.hidden != EquipmentController.Equipment.None ? Right : null;

        // The second and the hidden tool as kept, also while the tools do not aim with the hand.
        public static EquipmentController.Equipment Second => SecondHand != null ? SecondHand.second : EquipmentController.Equipment.None;
        public static EquipmentController.Equipment Hidden => HiddenHand != null ? HiddenHand.hidden : EquipmentController.Equipment.None;

        public readonly VrInput.Hand Input;
        public HandState State { get; private set; }
        // The tool in the hand: the game's (GameTool), the second one (SecondTool) or the hidden one (HiddenTool).
        public EquipmentController.Equipment Tool { get; private set; }
        // The tool belt zone the hand is in this frame, where its grip draws a tool (VrToolInput).
        public VrToolBelt.Zone Zone = VrToolBelt.Zone.None;

        private EquipmentController.Equipment second;
        private EquipmentController.Equipment hidden;
        private bool holding;

        // From VrToolInput.Update: whether the tools aim with the hand, and the game's equipped tool (None without one).
        private static bool aiming;
        private static EquipmentController.Equipment gameTool;
        private static int logs;

        private VrHand(VrInput.Hand input) => Input = input;

        public XRNode Node => Input.Node;
        public string Name => Input.Name;
        public bool IsLeft => Input.IsLeft;
        public VrHand Other => IsLeft ? Right : Left;

        // A tool that this hand works (VrControls: a left hand with one does not fly). A hidden tool rests.
        public bool HoldsTool => State == HandState.GameTool || State == HandState.SecondTool;

        // Free to hold on (VrClimb): nothing in the hand, not at the belt, where the grip draws a tool, and not fitting a
        // tool (VrToolFit, where the other hand's grip moves the tool).
        public bool Free => State == HandState.Empty && Zone == VrToolBelt.Zone.None && !VrToolFit.Blocks(Node);

        // ---- Bringing the state up to date with the game ----

        // From VrToolInput.Update, once per frame before the hands are read, with the game's equipped tool (None without
        // one). Returns whether the tools aim with the hand (VrAim.CanAim on the game's tool hand).
        public static bool Update(EquipmentController.Equipment equipped)
        {
            // A left controller that is gone (switched off) holds nothing.
            if (GameToolHand == Left && !VrInput.Left.Valid) GameToolHand = Right;
            Apply(VrAim.CanAim(GameToolHand.Node), equipped);
            return aiming;
        }

        // The game's side of the state: whether the tools aim with the hand, and the equipped tool.
        internal static void Apply(bool aimingNow, EquipmentController.Equipment equipped)
        {
            aiming = aimingNow;
            gameTool = equipped;
            Refresh();
        }

        private static void Refresh()
        {
            foreach (var hand in Both)
            {
                var state = HandState.Empty;
                var tool = EquipmentController.Equipment.None;
                if (hand.holding) state = HandState.Holding;
                else if (aiming && hand.second != EquipmentController.Equipment.None) { state = HandState.SecondTool; tool = hand.second; }
                else if (aiming && hand.hidden != EquipmentController.Equipment.None) { state = HandState.HiddenTool; tool = hand.hidden; }
                else if (aiming && hand == GameToolHand && gameTool != EquipmentController.Equipment.None) { state = HandState.GameTool; tool = gameTool; }
                if (state != hand.State || tool != hand.Tool)
                {
                    if (logs++ < 100)
                        Plugin.Log.LogInfo($"Hands: {hand.Name} {Text(hand.State, hand.Tool)} -> {Text(state, tool)}");
                    hand.State = state;
                    hand.Tool = tool;
                }
            }
        }

        private static string Text(HandState state, EquipmentController.Equipment tool) =>
            tool == EquipmentController.Equipment.None ? state.ToString() : $"{state} ({tool})";

        // ---- Transitions ----

        // This hand works the game's tool from now on, which is to be that tool (drawn or taken over; the game equips a
        // drawn tool later in the frame, and the next Update takes the game's word for it).
        public static void TakeGameTool(VrHand hand, EquipmentController.Equipment tool)
        {
            StopHoldingForTool(hand);
            GameToolHand = hand;
            gameTool = tool;
            Refresh();
        }

        // The game's tool is put away: the right hand is the tool hand again.
        public static void ResetGameTool()
        {
            if (GameToolHand == Right) return;
            GameToolHand = Right;
            Refresh();
        }

        // The hand keeps that tool as the second one; another second tool, in either hand, ends.
        public static void KeepSecond(VrHand hand, EquipmentController.Equipment tool)
        {
            StopHoldingForTool(hand);
            Left.second = Right.second = EquipmentController.Equipment.None;
            hand.second = tool;
            Refresh();
        }

        // The second tool is put away, or the game took it as its own (in the hand that holds it, if intoHand).
        public static void DropSecond(bool intoHand = false)
        {
            var hand = SecondHand;
            if (hand == null) return;
            if (intoHand)
            {
                GameToolHand = hand;
                gameTool = hand.second;
            }
            Left.second = Right.second = EquipmentController.Equipment.None;
            Refresh();
        }

        // The scanner hid that hand's tool.
        public static void Hide(VrHand hand, EquipmentController.Equipment tool)
        {
            StopHoldingForTool(hand);
            Left.hidden = Right.hidden = EquipmentController.Equipment.None;
            hand.hidden = tool;
            Refresh();
        }

        // The hidden tool comes back into its hand as the game's tool (intoHand), or stays put away.
        public static void Unhide(bool intoHand = false)
        {
            var hand = HiddenHand;
            if (hand == null) return;
            if (intoHand)
            {
                GameToolHand = hand;
                gameTool = hand.hidden;
            }
            Left.hidden = Right.hidden = EquipmentController.Equipment.None;
            Refresh();
        }

        // Holds on, if the hand is free; false otherwise.
        public static bool StartHolding(VrHand hand)
        {
            if (!hand.Free) return false;
            hand.holding = true;
            Refresh();
            return true;
        }

        public static void StopHolding(VrHand hand)
        {
            if (!hand.holding) return;
            hand.holding = false;
            Refresh();
        }

        // A hand that gets a tool lets go first. Its grip was let go and pressed again to draw, so this should not happen;
        // logged if it does.
        private static void StopHoldingForTool(VrHand hand)
        {
            if (!hand.holding) return;
            Plugin.Log.LogWarning($"Hands: the {hand.Name} hand gets a tool while holding on, lets go");
            hand.holding = false;
        }

        // For the F8 dump.
        public static string Describe() =>
            $"hands: left {Text(Left.State, Left.Tool)}, right {Text(Right.State, Right.Tool)}, game tool hand {GameToolHand.Name}";
    }
}
