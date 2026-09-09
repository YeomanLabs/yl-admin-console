# ACCESSIBILITY — M8.4 pass

Checked 2026-09-09 against the M8.4 targets: every control keyboard-reachable, an accessible name on anything with no
visible text, and text contrast at or above 4.5:1.

Two audits back this up, both in the scratchpad harness rather than the repo:

- a static pass over every `Views\*.xaml`, checking each interactive element for either visible text or an
  `AutomationProperties.Name`;
- a live pass that walks the running app's automation tree page by page and reports anything unnamed or not
  keyboard-focusable.

## Names

**115 interactive elements in the XAML, none without a name.** Every icon-only button carries an
`AutomationProperties.Name`; every text box, combo box and checkbox is either labelled by its content or named.

Fixed in this pass: the navigation rail, the notification stack, the palette's result list and the User 360 tab strip
were unnamed containers. A screen reader landed on them and had nothing to say.

## Keyboard

Every control the app itself defines is keyboard-reachable. Three things are reported by the audit and are not defects:

| Reported | Why it is not a defect |
|---|---|
| `PART_MinimizeButton`, `PART_MaximizeButton`, `PART_CloseButton` | WPF-UI's own title-bar template. Windows exposes these through the window itself (Alt+Space), as it does for every app using this control. Naming them would mean forking the template. |
| Scroll bar `PageUp` / `PageDown` / `PART_ButtonScrollUp` / `PART_ButtonScrollDown` | No WPF scroll bar's repeat buttons take focus. Scrolling is done with the arrow and page keys on the focused content. |
| Palette result rows | Deliberate, per UX §3: the search box keeps focus, arrows move the selection and Enter opens. Tabbing through eight rows to reach the ninth would be slower for everyone, including a keyboard-only user. |

The palette rows not taking focus did leave a real gap: a screen reader had no way to know which row was selected. The
footer is now an `AutomationProperties.LiveSetting="Polite"` region that names the selection, so moving the arrows
announces "3 results · 4.9 ms · Andrew Yeoman selected".

## Contrast

Computed with the WCAG relative-luminance formula against the palette in `Views\Theme.xaml`.

| Text | On | Ratio | Needs | Verdict |
|---|---|---|---|---|
| Coral `#FF6950` | Navy chrome `#041635` | 6.31:1 | 4.5:1 | pass |
| Coral `#FF6950` | Dark page `#1F1F1F` | 5.80:1 | 4.5:1 | pass |
| Danger red `#F04E4E` | Dark page `#1F1F1F` | 4.63:1 | 4.5:1 | pass |
| Coral `#FF6950` | White `#FFFFFF` | 2.84:1 | 4.5:1 | fails, and is never used |

Coral is used as text in exactly one place, the status bar, which sits on the navy chrome in every theme. The white-
background figure is recorded so nobody later moves coral text onto a light surface believing it is safe.

## Light theme

Selecting Light used to leave the chrome unreadable: the navy rail, status bar and quick-facts panel kept their navy
background while their text switched to the light theme's dark foreground. The chrome now carries an explicit light
foreground in both themes, which is the honest fix given that the chrome is navy by design (UX §1).

Two things found on the way, both fixed:

- Settings were serialized with enums as numbers, so a hand-edited `"theme": "Light"` failed to parse and was silently
  replaced by the default. The file now writes enum names, and a test pins it. This is also why the first light-theme
  check appeared to pass when nothing had changed.
- The copy-to-clipboard mono style had a fixed foreground, so it stayed dark on the navy sidebar. It now inherits.

## Not covered

- No screen reader was actually driven. The automation tree is what a screen reader reads, so the names and the live
  region are verified, but the spoken experience with Narrator or NVDA has not been sat through end to end.
- High-contrast mode is untested.
- Density affects only the row-height resources; no page has been checked at Compact for clipping.
