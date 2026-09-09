# 01 — Product

## One sentence
A Windows desktop console that lets a helpdesk or sysadmin find any person or device in a Microsoft 365 tenant in under a second and see their identity, access, and endpoints on one screen — then onboard or offboard them with a template or a checklist instead of six portals and PowerShell.

## Who it's for
- **Primary:** Tier-1/Tier-2 helpdesk and sysadmins at mid-market organizations (500–5,000 users), hybrid or cloud-only.
- **Secondary:** MSP technicians co-managing several tenants (Phase 3).
- **Also:** YeomanLabs consultants on Intune/Entra migration engagements — the tool is a sales asset and a delivery tool.

## The problem
Answering "what's going on with this user?" today means the Entra portal for identity, the Intune portal for devices, the M365 admin center for licenses, EXO for mailbox, and ADUC for on-prem — each slow, each with its own search. Onboarding/offboarding is a manual checklist that gets done differently every time.

## What already exists
EasyEntra (Agema A/S) does this for users + mailboxes with an ADUC-style UI. It is good and fast. We are not cloning it. Our differences:

1. **Devices are first-class.** A user's Intune-managed devices — compliance, encryption, Autopilot, last check-in, primary user — sit on the same screen as their account. Device → owner → other devices is one click each way.
2. **Search-first, not tree-first.** A command palette is the home screen. The OU/group tree exists but is secondary.
3. **Sync state is always visible** (Phase 2). Hybrid admins should never wonder "did that sync yet."
4. **Modern UI**, dark by default, keyboard-driven, but the mental model (object list → property sheet → actions) stays familiar.

## Principles (in priority order)
1. **Fast.** If a screen takes more than 300 ms to become interactive, that's a bug.
2. **Honest.** Show real state, real errors, real consequences. Never a silent failure or a silent disabled control.
3. **Safe.** Preview before destructive actions. Reversible where possible. Logged always.
4. **Zero-training.** A competent tier-1 tech should be productive in ten minutes without docs.
5. **Trustworthy posture.** No infra, no service accounts, no third-party data path. Same audit trail as the portals.

## Non-goals for Phase 1
- On-prem AD (Phase 2). Exchange Online mailbox operations (Phase 3). Multi-tenant switching (Phase 3).
- Reporting/dashboards. Bulk CSV operations. Scheduling. HRIS integration.
- Licensing enforcement, telemetry, auto-update, installer polish. (Ship as a signed MSIX later; for now `dotnet run`.)
- Anything the portal already does well that isn't on the user/device/onboard/offboard path.

## Success criteria for Phase 1 (dogfood)
- Cold start to interactive palette: < 3 s (cache warm), < 10 s to signed in on first run.
- Palette query → results: < 100 ms at 5,000 users, 2,000 devices, 1,000 groups.
- User 360 screen fully populated: < 500 ms from cache, live fields (sign-in activity, device check-in) refreshed in background within 2 s.
- Create user from template: < 30 s wall clock including license assignment.
- Offboard user with default steps: < 60 s, every step logged with outcome.
- Zero UI-thread stalls > 100 ms during sync.
