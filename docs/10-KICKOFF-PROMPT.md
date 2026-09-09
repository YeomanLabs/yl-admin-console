# 10 — Kickoff prompt for Claude Code

Open Claude Code in the repo root on your Windows machine (with .NET 8 SDK installed, and `appsettings.json` already holding your client ID from doc 08). Paste:

---

Read `CLAUDE.md`, then `docs/00-README.md`, `docs/01-PRODUCT.md`, `docs/03-ARCHITECTURE.md`, and `docs/09-PLAN.md` in full before doing anything. Skim the other docs so you know what they contain; you'll read them fully when a task points to them.

Then start on `docs/09-PLAN.md` milestone M0, task 0.1. Work one task at a time: implement, verify exactly as the task and the milestone gate require, check the box in the plan with a one-line note, commit with message `M0.1: <summary>`. Then move to the next task. Stop and ask me before: adding any NuGet package not already in a csproj, deviating from a spec doc, or anything that would require an Entra role or permission beyond what's in `docs/08-APP-REGISTRATION.md`.

When you finish a milestone, run its gate, paste me the evidence (test output, timings, screenshots where 🖥), and wait for my go before starting the next milestone.

If anything in the docs is wrong or impossible — an endpoint that 404s, a property delta doesn't support, a WPF-UI API that doesn't exist in 3.0.5 — stop, tell me what you found and what you propose, and log it in `docs/DECISIONS.md` once we agree.

Begin.

---

## Tips for running it
- Give Claude Code the dev tenant to test against from M1 onward. It will need you to complete the interactive sign-in the first time (WAM prompt). After that MSAL's cache handles it.
- Keep milestones as review points. M3's gate (palette < 100 ms) and M5's gate (writes visible in portal, undo works) are the two that most deserve your eyes.
- When it reports a spec conflict, resolve it in the doc first, then tell it to continue — the docs stay the source of truth, not the chat.
- Capture real Graph JSON from your tenant into `tests/Fixtures/` early (M2.2); scrub display names. Realistic fixtures catch far more than synthetic ones.
