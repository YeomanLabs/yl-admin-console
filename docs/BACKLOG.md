# BACKLOG — what is known to be rough

This is the list of things that are wrong, unverified, or annoying. It is not a feature wish list: Phase 2 (on-prem AD)
and Phase 3 (Exchange Online) are planned work and live in `docs/09-PLAN.md`, not here.

Started 2026-09-09, at the end of M8, seeded from what building Phase 1 turned up. The M8 gate is a full workday of
real helpdesk work; every friction point that day hits goes in below, dated, in the words it was felt in.

Each entry says what it is, how it was found, and what it would take to close. Nothing here is fixed by writing it down.

---

## Unverified — things that pass on paper but have not been sat through

### The offboarding wall clock has never been measured
Doc 01 targets a full offboard under 60 seconds. `docs/PERF.md` records this as open. The only account authorised for
live writes is the admin's own, and a full default run would sign the session out and destroy its own MFA
registrations, so the M6 gate timed only the steps that were safe to run. **To close:** a disposable licensed test
user, and one timed default run against it.

### No screen reader has actually been driven
The M8.4 audit walks the automation tree, which is what a screen reader reads, so every name and the palette's live
region are verified as *present*. Nobody has listened to Narrator or NVDA read a User 360 page top to bottom. The
difference matters: a correct tree can still be read in an order that makes no sense. **To close:** an hour with
Narrator on the palette, User 360, the offboard dialog and the create-user dialog.

### High-contrast mode is untested
The app defines its own brushes throughout. Windows high contrast overrides system colours, not custom
`SolidColorBrush` resources, so it is likely the app simply ignores it and stays navy. That may be acceptable; it has
not been looked at. **To close:** run under each high-contrast theme and decide whether to honour it or say plainly in
Settings that it is not supported.

### Compact density has not been checked for clipping
Density changes the row-height resources only. No page has been opened at Compact and inspected for text cut off,
particularly the User 360 tab strip and the offboard step rows, which are the tightest layouts in the app.
**To close:** a pass over every page at Compact.

### UI-thread stalls during sync are judged by eye
Doc 01 allows no stall over 100 ms. Through the M2 and M3 gates the window stayed responsive during a full initial
walk, but "stayed responsive" is a person watching, not a number. On an 11-user tenant it proves very little.
**To close:** a dispatcher-latency counter, and a run against a tenant large enough to matter. Worth doing before this
is pointed at a real customer.

### The destructive actions have never been fired
Retire, Wipe, Restart, auth-method delete, revoke sessions and password reset are built, unit-tested, and driven
through their dialogs as far as the confirm step. None has been executed against a real device or account, by
instruction. The confirm dialogs and the preview text are verified; the Graph call and its error mapping are not.
**To close:** a spare enrolled device and a disposable account.

---

## Rough edges found while building

### Nine of the nineteen offboarding steps cannot run yet
Ten are Phase 1. Nine need Exchange Online or on-prem AD and are listed, greyed, and labelled with the phase that will
bring them. This is honest and it is also the single biggest gap between what the offboard dialog *looks* like it does
and what it does. A leaver still needs a second tool for the mailbox. Watch during the gate whether the greyed rows
read as reassuring or as a tease.

### User templates are edited as raw JSON
The Templates page shows the file and lets it be edited in place, which was the right call for M7 — the file is the
truth and a form would have hidden fields the file supports. But the first time a template is written by hand, a
missing comma is a parse error rather than a red field. **To close:** either a form over the common fields with the
raw file behind a toggle, or inline error positions from the parser.

### Intune error text is Intune's, not ours
Intune's error bodies have no `error.code`, so `docs/02-UX-SPEC.md §7`'s friendly-text map cannot key off anything.
The whole JSON blob used to be shown to the admin; it is now parsed and truncated at the operation id, which is an
improvement and not a fix. Anything from the Intune surface still reads like a service message.
**To close:** collect the messages actually seen in a workday, then map the frequent ones.

### `signInActivity` needs Entra ID P1
On a tenant without it, last sign-in shows "Not available on this tenant's licence". That degrades correctly, but
last-sign-in is one of the more useful things on a User 360 page and its absence is invisible until you look for it.
Nothing to fix in the app; noted so it is not diagnosed twice.

### `managedDevices` has no delta
Intune device data is refreshed on an interval and on demand, so it can be up to the configured interval stale while
everything else on the page is delta-fresh. The page does not currently say which half is which.
**To close:** an "as of" stamp on the Intune section, or refresh that section on page open.

### WPF-UI's title-bar buttons carry no accessible name
Minimise, maximise and close come from WPF-UI's own window template. Windows exposes them through the window itself
(Alt+Space), as it does for every app using this control, so this is not a keyboard defect — but an automation tree
walker does report three unnamed buttons, and anyone auditing this app will report them too. Naming them means forking
the template. **To close:** decide whether to fork, or document it once here and stop re-finding it.

---

## From the workday

Nothing yet — the M8 gate has not been run. New entries go here with the date and the task that was being done at the
time, not tidied into the sections above until they are understood.
