#!/usr/bin/env python3
"""Draws the control overviews in docs/images (flying, hands, tool buttons, cutter, demo charges, grapple, menus) as SVG.

Run it after changing a control: python3 tools/control-images.py
The texts here are the players' view of the controls; keep them in line with README.md and docs/INSTALL.md.
"""
from html import escape
from pathlib import Path

OUT = Path(__file__).resolve().parent.parent / "docs" / "images"

BG = "#11151b"
CARD = "#1a2028"
CARD_EDGE = "#2b333d"
PART = "#2f3945"
PART_DARK = "#3a4554"
ACCENT = "#f0883e"
TEXT = "#e6edf3"
MUTED = "#8b949e"
LASER = "#8cf2ff"
TETHER = "#ff9926"
FONT = "Segoe UI, Helvetica, Arial, sans-serif"

WIDTH = 1200
CARD_W = 550
CARD_H = 120
GAP = 20
TOP = 110


def text(x, y, value, size=13.5, color=TEXT, bold=False, anchor="start"):
    weight = "bold" if bold else "normal"
    return (f'<text x="{x}" y="{y}" font-size="{size}" fill="{color}" font-weight="{weight}" '
            f'text-anchor="{anchor}" font-family="{FONT}">{escape(value)}</text>')


def arrow(d, color=ACCENT, width=2.5):
    return f'<path d="{d}" stroke="{color}" stroke-width="{width}" fill="none" marker-end="url(#ah)"/>'


# Icons, drawn in a 76x76 box.

def controller(tilt=0, ghost=False, dx=0, dy=0):
    opacity = ' opacity=".45"' if ghost else ""
    return (f'<rect x="{23 + dx}" y="{14 + dy}" width="30" height="48" rx="12" fill="{PART}" stroke="{ACCENT}" '
            f'stroke-width="2" transform="rotate({tilt} {38 + dx} {38 + dy})"{opacity}/>')


def button(label, r=20, size=18):
    return (f'<circle cx="38" cy="38" r="{r}" fill="{PART}" stroke="{ACCENT}" stroke-width="2.5"/>'
            + text(38, 38 + size / 2.8, label, size=size, bold=True, anchor="middle"))


def stick(directions):
    parts = [f'<circle cx="38" cy="38" r="30" fill="{PART}" stroke="{ACCENT}" stroke-width="2"/>',
             f'<circle cx="38" cy="38" r="12" fill="{PART_DARK}"/>']
    ends = {"left": "M14 38 H2", "right": "M62 38 H74", "up": "M38 14 V2", "down": "M38 62 V74"}
    parts += [arrow(ends[d], width=3) for d in directions]
    return "".join(parts)


def stick_press(label):
    return (f'<circle cx="38" cy="38" r="26" fill="{PART}" stroke="{ACCENT}" stroke-width="2.5"/>'
            f'<circle cx="38" cy="38" r="16" fill="{PART_DARK}"/>' + text(38, 44, label, size=15, bold=True, anchor="middle"))


def trigger():
    return (controller() + f'<path d="M30 30 Q22 38 30 48" stroke="{ACCENT}" stroke-width="4" fill="none"/>'
            + arrow("M10 39 H22"))


def grip():
    return (controller() + f'<rect x="47" y="30" width="8" height="20" rx="3" fill="{ACCENT}"/>'
            + arrow("M72 40 H60") + arrow("M4 40 H16"))


def laser(color, ring=False):
    parts = [controller(tilt=55, dx=-12, dy=8),
             f'<path d="M40 40 L74 18" stroke="{color}" stroke-width="3"/>']
    if ring:
        parts.append(f'<circle cx="70" cy="21" r="7" fill="none" stroke="{color}" stroke-width="3"/>')
    return "".join(parts)


def tether(color=TETHER):
    return (f'<rect x="2" y="8" width="16" height="26" rx="3" fill="{PART_DARK}"/>'
            f'<rect x="58" y="42" width="16" height="26" rx="3" fill="{PART_DARK}"/>'
            f'<path d="M18 22 L58 54" stroke="{color}" stroke-width="5"/>'
            f'<circle cx="18" cy="22" r="4" fill="{color}"/><circle cx="58" cy="54" r="4" fill="{color}"/>')


def trigger_and(label):
    return (trigger() + f'<circle cx="62" cy="14" r="13" fill="{PART}" stroke="{ACCENT}" stroke-width="2.5"/>'
            + text(62, 19, label, size=14, bold=True, anchor="middle"))


def two_buttons(a, b):
    return (f'<circle cx="22" cy="44" r="15" fill="{PART}" stroke="{ACCENT}" stroke-width="2.5"/>'
            + text(22, 50, a, size=15, bold=True, anchor="middle")
            + f'<circle cx="54" cy="28" r="15" fill="{PART}" stroke="{ACCENT}" stroke-width="2.5"/>'
            + text(54, 34, b, size=15, bold=True, anchor="middle"))


def two_tools():
    return controller(tilt=-15, dx=-14) + controller(tilt=15, dx=14)


def hands_swap():
    return (controller(tilt=-15, dx=-14) + controller(tilt=15, dx=14)
            + arrow("M24 8 Q38 -2 52 8"))


def holding():
    return (f'<rect x="0" y="54" width="76" height="14" rx="3" fill="{PART_DARK}"/>'
            + f'<path d="M24 54 V30 Q24 18 36 18 Q48 18 48 30 V54" fill="{PART}" stroke="{ACCENT}" stroke-width="2.5"/>'
            + arrow("M64 40 V8"))


def scanner():
    return (f'<rect x="14" y="16" width="48" height="44" rx="10" fill="{PART}" stroke="{ACCENT}" stroke-width="2"/>'
            f'<path d="M22 46 L32 32 L42 40 L54 24" stroke="{LASER}" stroke-width="2.5" fill="none"/>')


def wrist():
    return (controller(tilt=0) + f'<path d="M10 20 A34 34 0 0 1 66 20" stroke="{ACCENT}" stroke-width="2.5" '
            f'fill="none" marker-end="url(#ah)"/>' + f'<path d="M22 64 H54" stroke="{LASER}" stroke-width="3"/>')


def console():
    return (f'<rect x="8" y="10" width="42" height="56" rx="6" fill="{PART_DARK}"/>'
            f'<rect x="16" y="20" width="26" height="16" rx="3" fill="{PART}" stroke="{ACCENT}" stroke-width="2"/>'
            + arrow("M74 50 H52"))


def cut_line(vertical=False):
    line = "M38 6 V70" if vertical else "M6 38 H70"
    return (f'<rect x="14" y="14" width="48" height="48" rx="6" fill="{PART_DARK}"/>'
            f'<path d="{line}" stroke="{LASER}" stroke-width="3" stroke-dasharray="6 4"/>')


def stinger():
    return (f'<rect x="14" y="14" width="48" height="48" rx="6" fill="{PART_DARK}"/>'
            f'<circle cx="38" cy="38" r="8" fill="none" stroke="{LASER}" stroke-width="3"/>'
            f'<circle cx="38" cy="38" r="2.5" fill="{LASER}"/>')


def heat():
    return (f'<rect x="10" y="30" width="56" height="16" rx="5" fill="{PART_DARK}"/>'
            f'<rect x="12" y="32" width="36" height="12" rx="4" fill="{ACCENT}"/>'
            f'<rect x="48" y="32" width="16" height="12" rx="4" fill="#d1453b"/>')


def charge(thrown=False):
    parts = [f'<rect x="{8 if thrown else 22}" y="{40 if thrown else 26}" width="32" height="24" rx="5" fill="{PART}" '
             f'stroke="{ACCENT}" stroke-width="2"/>',
             f'<circle cx="{24 if thrown else 38}" cy="{52 if thrown else 38}" r="4" fill="#d1453b"/>']
    if thrown:
        parts.append(arrow("M44 44 Q60 30 70 12"))
    else:
        parts.append(f'<rect x="10" y="50" width="56" height="12" rx="3" fill="{PART_DARK}"/>')
    return "".join(parts)


def detonator():
    return (f'<rect x="24" y="18" width="28" height="46" rx="8" fill="{PART}" stroke="{ACCENT}" stroke-width="2"/>'
            f'<circle cx="38" cy="30" r="6" fill="#d1453b"/>'
            f'<path d="M58 16 L66 8 M60 28 L72 26 M14 16 L8 8" stroke="{ACCENT}" stroke-width="2.5"/>')


def card(x, y, icon, title, line, note):
    return (f'<rect x="{x}" y="{y}" width="{CARD_W}" height="{CARD_H}" rx="12" fill="{CARD}" stroke="{CARD_EDGE}"/>'
            f'<g transform="translate({x + 20},{y + 22})">{icon}</g>'
            + text(x + 110, y + 42, title, size=18, color=ACCENT, bold=True)
            + text(x + 110, y + 68, line)
            + text(x + 110, y + 88, note, color=MUTED))


def page(name, label, title, subtitle, cards, footer=None, heading=None):
    """Two columns of cards; a heading (column titles) above them if given, a footer line below."""
    top = TOP + (34 if heading else 0)
    rows = (len(cards) + 1) // 2
    height = top + rows * (CARD_H + GAP) + (50 if footer else 20)
    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{WIDTH}" height="{height}" viewBox="0 0 {WIDTH} {height}" '
             f'role="img" aria-label="{escape(label)}">',
             f'<rect width="{WIDTH}" height="{height}" rx="12" fill="{BG}"/>',
             '<defs><marker id="ah" viewBox="0 0 10 10" refX="7" refY="5" markerWidth="5" markerHeight="5" '
             f'orient="auto-start-reverse"><path d="M0 0 L10 5 L0 10 z" fill="{ACCENT}"/></marker></defs>',
             text(40, 52, title, size=26, bold=True),
             text(40, 78, subtitle, size=14, color=MUTED)]
    if heading:
        parts.append(text(40, TOP + 18, heading[0], size=16, color=heading[2], bold=True))
        parts.append(text(40 + CARD_W + GAP + 20, TOP + 18, heading[1], size=16, color=heading[3], bold=True))
    for i, (icon, card_title, line, note) in enumerate(cards):
        x = 40 + (i % 2) * (CARD_W + GAP + 20)
        y = top + (i // 2) * (CARD_H + GAP)
        parts.append(card(x, y, icon, card_title, line, note))
    if footer:
        parts.append(text(40, height - 28, footer, size=14, color=MUTED))
    parts.append("</svg>")
    (OUT / name).write_text("".join(parts) + "\n", encoding="utf-8")


def main():
    page("flying.svg", "ShipbreakerVR flying controls", "Flying in VR",
         "Controller flying (VR tab: Controller flying). Left hand: thrust, roll and brake. Right stick: turn and pitch.", [
             (controller(ghost=True, dx=-8, dy=8) + controller(dx=8, dy=-6) + arrow("M24 60 L56 22"),
              "Thrust", "Hold the left trigger and move the controller.", "Thrust goes where you move it; farther = stronger."),
             (controller(tilt=-25) + f'<path d="M8 20 A36 36 0 0 0 8 58" stroke="{ACCENT}" stroke-width="2.5" fill="none" marker-end="url(#ah)"/>',
              "Roll", "Trigger held: tilt the controller sideways.", "The farther you tilt, the faster you roll."),
             (stick(["left", "right"]), "Turn", "Right stick left / right.", "Smooth or 30° / 45° / 90° steps (VR tab: Turning)."),
             (stick(["up", "down"]), "Pitch", "Right stick up / down.", "Stick up looks up, like the mouse."),
             (button("Y"), "Brake", "Y on the left controller.", "Uses the game's own brake, fuel and HUD."),
             (stick_press("R3"), "Pause", "Press the right stick (also in the station).", "On loading screens it shows the next tip."),
             (stick_press("L3"), "Center the view", "Press the left stick, or F4 on the keyboard.", "Puts the view and the menus in front of you again."),
             (holding(), "Grapple holds something big", "Fly and brake as usual.", "Fly farther than the grapple reaches and the rope snaps."),
         ])

    page("hands.svg", "ShipbreakerVR hands", "Your hands",
         "Taking tools from the body and holding on. Either hand can do all of it.", [
             (grip(), "Take a tool", "Squeeze the grip at your body; it buzzes in a zone.",
              "Keep squeezing to hold it, let go to put it away."),
             (f'<circle cx="38" cy="14" r="10" fill="{PART}" stroke="{ACCENT}" stroke-width="2"/>'
              f'<rect x="24" y="26" width="28" height="36" rx="8" fill="{PART}" stroke="{ACCENT}" stroke-width="2"/>'
              f'<circle cx="56" cy="20" r="4" fill="{ACCENT}"/><circle cx="58" cy="34" r="4" fill="{ACCENT}"/>'
              f'<circle cx="52" cy="58" r="4" fill="{ACCENT}"/><circle cx="24" cy="58" r="4" fill="{ACCENT}"/>',
              "Where the tools are", "Right hip grapple · left hip demo charges",
              "Over the right shoulder cutter · right side of the helmet scanner"),
             (two_tools(),
              "Two tools at once", "Take a second tool with the other hand.",
              "Cutter, grapple, demo charges; each hand works its own."),
             (hands_swap(), "Hand a tool over", "Reach for the tool in one hand with the other one",
              "and squeeze the grip: that hand takes it over."),
             (scanner(), "Scan", "Take the scanner: the other hand's tool hides.",
              "Keep that hand's grip squeezed and it comes back after the scan."),
             (holding(), "Hold on and climb", "Grip with an empty hand close to anything holds on.",
              "Move the hand to pull yourself; let go moving to push off."),
             (console(), "Consoles and switches", "Look at it and press A or X.",
              "The grip only holds on there."),
             (two_buttons("X", "Y"), "Left hand", "A tool in the left hand: X works as A, Y as B.",
              "Flying pauses until you let go; the right stick still turns."),
         ], footer="The suit's hands follow your controllers and close with the grip.")

    page("tools.svg", "ShipbreakerVR tool buttons", "Tool buttons",
         "Aim with the hand (VR tab: Aim with the hand). The buttons are on the controller of the hand that holds the tool.", [
             (laser(LASER), "Aim", "Point the controller. A laser shows where.",
              "Empty hand: no laser; A uses the middle of the view."),
             (trigger(), "Use the tool", "Trigger: cut, grapple, place or detonate demo charges.",
              "Hold the trigger to keep hold with the grapple."),
             (button("A"), "Use", "A: use what you point at, with either hand.",
              "On the cutter A turns the cut; use the other hand."),
             (trigger_and("A"), "Throw", "Trigger held + A: throw what the grapple holds.",
              "Hold A to charge, let go to throw."),
             (button("B"), "Tool mode", "B: switch the tool's mode.",
              "Demo charges: detonator. Grapple: tethers (see below)."),
             (trigger_and("B"), "Reel in", "Trigger held + B: reel in what the grapple holds.",
              "While B is held."),
             (wrist(), "Turn the cutter's line", "A: horizontal or vertical cuts.",
              "Rolling your wrist turns the line too."),
             (two_buttons("X", "Y"), "Left hand", "X works as A, Y as B.",
              "Flying pauses while the left hand holds a tool."),
         ])

    page("cutter.svg", "ShipbreakerVR cutter", "Cutter",
         "Over the right shoulder. B switches between the Splitsaw (a line) and the Stinger (a point).", [
             (cut_line(), "Splitsaw", "Trigger: cut along the line.",
              "Starts with horizontal cuts."),
             (button("A"), "Turn the cut", "A: switch between horizontal and vertical cuts.",
              "The line also turns with your wrist."),
             (stinger(), "Stinger", "B: switch to the Stinger, B again: back.",
              "Trigger: cut the point the laser shows."),
             (heat(), "Heat", "The cutter heats up while it cuts.",
              "The heat bar shows it; let go before it overheats."),
             (two_tools(), "Cutter and grapple", "Hold the grapple in the other hand.",
              "Each hand's trigger and buttons work its own tool."),
             (grip(), "Fit it into the hand", "VR tab: Fit tools in the hand.",
              "The other hand's grip moves it; switch the row off when done."),
         ])

    page("charges.svg", "ShipbreakerVR demo charges", "Demo charges",
         "At the left hip. The charge sits in your hand; the laser shows where it goes.", [
             (charge(), "Place a charge", "Trigger while pointing at a surface.",
              "The game's marker shows where it sticks."),
             (charge(thrown=True), "Throw a charge", "Trigger while pointing at nothing.",
              "It flies where your hand points."),
             (button("B"), "Detonator", "B: take the detonator, B again: back to charges.",
              "Back only while charges are left."),
             (detonator(), "Detonate", "Trigger with the detonator in the hand.",
              "Blows all placed charges."),
             (two_tools(), "Charges and another tool", "Take the cutter or grapple with the other hand.",
              "Each hand's trigger and buttons work its own tool."),
             (two_buttons("X", "Y"), "Left hand", "Charges in the left hand: X works as A, Y as B.",
              "Flying pauses until you let go."),
         ])

    page("grapple.svg", "ShipbreakerVR grapple and tethers", "Grapple and tethers",
         "B switches the grapple between grappling and placing tethers. Two buzzes: tether mode, one buzz: grappling.", [
             (laser(LASER), "Blue laser", "The grapple grapples.", "As in the flat game."),
             (laser(TETHER, ring=True), "Orange laser with a ring", "The grapple places tethers.",
              "The ring marks where the tether would hold."),
             (trigger(), "Grapple", "Trigger: grapple, hold to keep hold.",
              "Trigger held + A throws, trigger held + B reels in."),
             (trigger(), "Place a tether", "Trigger at the start, let go at the end.",
              "A line shows the tether while the trigger is held."),
             (button("B"), "Tether with B", "Hold B at the start, let go at the end.",
              "Hold B pointing at nothing: recall all tethers."),
             (tether(), "Remove one tether", "Point at it (it lights up orange) and tap A.",
              "Pointing at no tether, A uses what you point at."),
             (button("B"), "Tether mode", "B: switch to the tether mode.", "A tap."),
             (button("A"), "Recall all tethers", "Hold A.", "B switches back to grappling, however long you hold it."),
         ], heading=("Grappling", "Placing tethers", LASER, TETHER))

    page("menus.svg", "ShipbreakerVR menu controls", "Menus and keyboard",
         "In menus both controllers act as one Xbox gamepad, each button where its twin sits.", [
             (stick(["left", "right", "up", "down"]), "Left stick", "Navigate.", "Pressed: center the view."),
             (stick(["up", "down"]), "Right stick", "Scroll.", "Pressed: R3 in menus, pause in gameplay."),
             (two_buttons("B", "A"), "A / B", "A confirms, B goes back.", "X and Y are X and Y."),
             (trigger(), "Triggers", "Switch tabs (the gamepad's shoulder buttons).",
              "The grips are the gamepad's triggers."),
         ], footer="Keyboard: F3 VR on/off · F4 center view · F7 monitor look · F8 debug dump · "
                   "The left menu button stays with Virtual Desktop.")


if __name__ == "__main__":
    main()
