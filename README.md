# ShipbreakerVR

Play **Hardspace: Shipbreaker** in VR: look around your cutting bay with your own head, fly with one hand, take your
tools off your suit and aim them with your hands, and use the menus with your motion controllers.

**[Download the latest version](https://github.com/marczahn/shipbreaker-vr/releases/latest)**

> **Early test version (alpha).** The mod is playable, but expect rough edges and missing pieces. This is an
> unofficial fan project, not made or supported by the game's developers. Use it at your own risk, see the
> [disclaimer](DISCLAIMER.md).

- [What you get](#what-you-get)
- [What you need](#what-you-need)
- [Installing](#installing)
- [Playing](#playing)
- [Settings](#settings)
- [Something is wrong](#something-is-wrong)
- [Questions](#questions)
- [What does not work yet](#what-does-not-work-yet)
- [Getting help and reporting bugs](#getting-help-and-reporting-bugs)
- [Uninstalling](#uninstalling)
- [Thanks](#thanks)

## What you get

- **Real VR from the first second.** The game starts in your headset, intro and loading screens included. Look around
  freely; your body turns with your head.
- **Fly with your hand.** Hold the trigger on the left controller and move your hand where you want to go. The right
  stick turns you, Y brakes.
- **Tools in your hands.** Grab the grapple, cutter, demo charges and scanner from your hips, shoulder and helmet with
  either hand, one in each hand if you like, and aim them where your hand points. A thin laser shows where. Hold on to
  anything with an empty hand and climb along it. The grapple has a second mode for placing tethers. The suit's hands
  follow your controllers, and the cutter's plain right side, never seen in the flat game, gets the detail of its left
  side.
- **Menus with the controllers.** No need to reach for the mouse: your controllers work like a gamepad in all menus.
- **Your HUD inside the helmet.** The game's display sits in your visor, menus float in the room in front of you, and
  the helmet frame moves with your head.
- **All VR settings in the game.** A new "VR" tab in the game's options, with a short explanation for every setting.
- **Mouse, keyboard and gamepad still work**, if you prefer them for some things.

## What you need

- **Hardspace: Shipbreaker on Steam** (Windows). Other store versions have not been tested.
- **A gaming PC that can run VR.** The mod runs on the PC, not on the headset itself. It is tested with an
  RTX 4070 Ti; on weaker graphics cards you will likely need lower settings.
- **A PC VR headset**, for example:
  - Meta Quest 2, 3 or Pro connected through Virtual Desktop (this is what the mod is tested with), Quest Link or
    Air Link
  - Valve Index, HTC Vive and other SteamVR headsets (not tested yet, but should work)
- **VR controllers** to fly and use the menus. Without them you can still play with mouse and keyboard or a gamepad
  and only use the headset to look around.
- **Good VR legs.** You float in zero gravity and turn in every direction. If VR easily makes you feel sick, start
  with short sessions.

The game does not run in a standalone headset (a Quest without a PC).

## Installing

Before you start, make a copy of your save games, just in case.

1. **Download** the ZIP file from the [latest release](https://github.com/marczahn/shipbreaker-vr/releases/latest)
   (the file called `ShipbreakerVR-<version>.zip`).
2. **Open the game folder:** in your Steam library, right-click Hardspace: Shipbreaker, then choose
   **Manage → Browse local files**. A folder opens that contains `Shipbreaker.exe`.
3. **Unpack the ZIP into that folder.** Everything in the ZIP goes right next to `Shipbreaker.exe`. If Windows asks
   whether to replace files or merge folders, say yes.
4. **Start the game** as usual from Steam, with your headset connected and your VR app running (for example Virtual
   Desktop). The first start takes a little longer.

That's it. Updating works the same way: unpack the new ZIP over the old one.

More details and a list of everything the ZIP contains: [installation guide](docs/INSTALL.md).

## Playing

### Flying (left hand flies, right hand steers)

![Flying controls](docs/images/flying.svg)

| I want to … | How |
|---|---|
| Fly in a direction | Hold the left trigger and move the controller that way. The farther you move it, the faster you go. |
| Roll | Keep the left trigger held and tilt the controller sideways. |
| Turn left or right | Push the right stick left or right. |
| Look up or down | Push the right stick up or down. |
| Brake | Press Y. |
| Open the pause menu | Press the right stick (also in the station). |
| Put the view back in front of me | Press the left stick (or F4 on the keyboard). |
| Fly while the grapple holds something big | Fly and brake as usual. If you fly farther away than the grapple reaches, the rope snaps. |

### Your hands

![Hands](docs/images/hands.svg)

| I want to … | How |
|---|---|
| Take a tool | Reach to where it sits and squeeze the grip: grapple at the right hip, demo charges at the left hip, cutter over the right shoulder, scanner at the right side of the helmet. The controller buzzes when you are there. |
| Put it away | Let go of the grip. |
| Hold two tools at once | While one hand holds the cutter, the grapple or the demo charges, take another of them with your other hand. Each hand's trigger and buttons work its own tool. |
| Hand a tool to the other hand | Reach for it with the other hand and squeeze the grip. |
| Scan with a tool in the other hand | Take the scanner with your free hand. The other tool disappears while you scan; keep its grip squeezed and it comes back when you let go of the scanner. |
| Hold on and climb | Squeeze the grip of a hand without a tool close to anything: the hand holds on there. Move the hand to pull yourself along, with both hands hand over hand, or with one while the other works a tool. Light parts come to your hand instead; the station and fixed hulls stay put. Let go while moving the hand to push off. |
| Work an airlock console or a switch | Look at it and press A or X. The grip only holds on, also there. |
| Use a tool with the left hand | Take it with the left hand. X and Y then do what A and B do, and flying pauses until you let go. |

### Tool buttons

![Tool buttons](docs/images/tools.svg)

| I want to … | How |
|---|---|
| Use it | Point the controller and pull the trigger: cut, grapple, place or detonate demo charges. |
| Use what I point at | Press A or X, with either hand. With an empty hand it uses what is in the middle of your view, marked by the game's dot. Without a tool the trigger and B do nothing. |
| Throw | Hold the trigger and press A. |
| Reel in what the grapple holds | Hold the trigger and press B. |
| Switch the tool's mode | Press B (demo charges: switch to the detonator; grapple: see below). |
| Turn the cutter's line | Roll your wrist. The cutter starts with horizontal cuts, and its switch to vertical cuts has no button. |

### Grapple and tethers

![Grapple and tethers](docs/images/grapple.svg)

B switches the grapple between grappling (blue laser) and placing tethers (orange laser with a ring where it points).
The controller buzzes twice when the tether mode starts and once when grappling comes back.

| I want to … | Grappling (blue laser) | Placing tethers (orange laser) |
|---|---|---|
| Place a tether | Hold B while pointing at the start, let go at the end. | Pull the trigger at the start, let go at the end. |
| Remove one tether | – | Point at it (it lights up orange) and tap A. |
| Recall all tethers | Hold B while pointing at nothing. | Hold A. |
| Switch the mode | Tap B. | Press B, however long. |

### Menus and keyboard

![Menus and keyboard](docs/images/menus.svg)

**In menus** both controllers act together like one Xbox gamepad: left stick to move around, A to confirm, B to go
back, the triggers switch tabs.

**Keyboard:** F3 turns VR on and off, F4 puts the view back in front of you. Both keys can be changed in the config file (section `[Tasten]`).

## Settings

Open the game's options and choose the **VR** tab. Every row explains itself when you select it. The ones you are most
likely to need:

- **Center view:** puts the view and the menus in front of you again.
- **Render resolution:** how sharp the picture is. Lower it if the game stutters.
- **Station resolution:** the same for the station between shifts, which is harder on the graphics card.
- **Turning:** smooth turning, or turning in steps, which is easier on the stomach.
- **Head height** and **Head sideways:** move your view if you seem to sit too low or off to one side in the suit.
- **Controller flying:** switch it off if you fly with mouse and keyboard or a gamepad.
- **Aim with the hand:** switch it off to aim your tools at the center of your view again, as in the flat game.
- **Two tools at once:** switch it off if you want a tool from one hand to replace the one in the other hand.
- **Hold on and climb:** switch it off if the grip of an empty hand should do nothing.
- **Fit tools in the hand:** if the cutter or the grapple does not sit right in your hand, switch this on, grab the
  tool with your other hand and move it into place. Letting go stores it. Switch it off again when you are done.

## Something is wrong

### The game crashes right after loading into a shift
The render resolution is probably set too high and the graphics card runs out of memory. Set **Render resolution**
in the VR tab back to 1.0.

### The game stutters or the picture smears when I move
Lower **Render resolution** in the VR tab, and leave **Ambient occlusion** on Low or switch it off. In the game's own
graphics settings, a lower quality level helps too.

### The view wobbles at the station's monitors
The graphics card does not keep up there. Lower **Station resolution** in the VR tab step by step until it stops.

### I sit too low or off to one side in the suit
Use **Head height** and **Head sideways** in the VR tab.

### The menu or the view is behind me or off to the side
Press **B and Y** together, or **F4**. This puts everything in front of you again.

### The game starts, but not in the headset
Make sure the headset is connected and your VR app (Virtual Desktop, the Meta app or SteamVR) is running before you
start the game. Then restart the game.

### Nothing changed at all, the game looks like before
The files probably ended up in the wrong place. Check that the game folder now contains a folder called `BepInEx`
right next to `Shipbreaker.exe`. If the ZIP was unpacked into a subfolder of its own, move its contents up.

## Questions

**Are my save games safe?**
The mod does not touch your save games. It is still a test version, though, so a backup never hurts.

**Can I switch VR off without uninstalling?**
Yes, press F3 in the game.

**Can I play with mouse and keyboard or a gamepad?**
Yes. They keep working while you look around with the headset. Switch off **Controller flying** in the VR tab if you
don't use the motion controllers.

**Is this the same as the ShipbreakerVr mod by Raicuparta?**
No. Raicuparta's mod was an earlier, simpler VR mode for the same game without motion controls; this mod builds on
parts of it. Use only one of the two at a time.

**Does it work with other mods?**
That has not been tested.

## What does not work yet

- The cutter's switch between horizontal and vertical cuts has no button; roll your wrist instead.
- There is no laser pointer for the menus yet; you move through them with the sticks.
- During loading screens the view can go dark for a moment.
- Above 1.0, the render resolution can crash the game after loading (see above).

## Getting help and reporting bugs

1. Look through the [existing reports](https://github.com/marczahn/shipbreaker-vr/issues?q=is%3Aissue) first. If
   your problem is already there, add what you saw.
2. Otherwise [open a new report](https://github.com/marczahn/shipbreaker-vr/issues/new) (needs a free GitHub
   account). Describe what you did and what happened.
3. Please attach the file `LogOutput.log`. You find it in the game folder (see step 2 of
   [Installing](#installing)), inside the folder `BepInEx`. It is rewritten every time you start the game, so copy it
   right after the problem happened.

## Uninstalling

To switch off the mod, delete the file `winhttp.dll` in the game folder. To remove it completely, also delete the
`BepInEx` folder there. The [installation guide](docs/INSTALL.md#uninstall) lists the few remaining files, which do
nothing on their own.

## Thanks

- **Raicuparta**, whose [ShipbreakerVr](https://github.com/Raicuparta/shipbreaker-vr) mod showed how to get VR into
  this game; our VR setup builds on it.
- **[Bobmightypirate](https://github.com/Bobmightypirate)**, for testing and ideas.
- **BepInEx** and **Unity's OpenXR plugin**, which make the mod possible.
- **Blackbird Interactive**, for making the game.

Health notes on VR use, liability and the legal details: [disclaimer](DISCLAIMER.md).

---

**For developers:** how to build the mod, how it works and how it came about: [CONTRIBUTING.md](CONTRIBUTING.md).
License: MIT, see [LICENSE](LICENSE); included third-party components keep their own licenses ([licenses/](licenses)).
