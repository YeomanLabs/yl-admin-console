# Docs index

| # | File | Read it when |
|---|------|-------------|
| 01 | `01-PRODUCT.md` | Understanding what we're building, for whom, and what "good" means |
| 02 | `02-UX-SPEC.md` | Building any screen, dialog, state, or error message |
| 03 | `03-ARCHITECTURE.md` | Touching project structure, DI, threading, auth flow, or sync loop |
| 04 | `04-GRAPH-REFERENCE.md` | Writing anything that calls Microsoft Graph |
| 05 | `05-DATA-MODEL.md` | Touching models, the SQLite schema, or the hybrid read-only rules |
| 06 | `06-OFFBOARDING-CATALOG.md` | Implementing or changing offboarding steps |
| 07 | `07-TEMPLATES.md` | Implementing user templates or user creation |
| 08 | `08-APP-REGISTRATION.md` | Setting up the Entra app registration (human task, once) |
| 09 | `09-PLAN.md` | Always — this is the task list you work from |
| 10 | `10-KICKOFF-PROMPT.md` | The prompt to paste into Claude Code to begin |
| 11 | `11-ONPREM-AD.md` | Anything that talks to on-prem Active Directory (Phase 2) |
| 12 | `12-ENTRA-CONNECT-LAB.md` | Working with the lab DC and its Entra Connect sync |
| 13 | `13-EXCHANGE-ONLINE.md` | Anything that talks to Exchange Online (Phase 3) |
| — | `DECISIONS.md` | Append-only log of choices made where the spec was silent |

Phases: **Phase 1** (this plan) is cloud-only: Entra users/groups/devices + Intune devices, search, User 360 view, template create, checklist offboarding. **Phase 2** adds on-prem AD and sync-state awareness. **Phase 3** adds Exchange Online depth, OneDrive, and multi-tenant switching (M13–M20 in doc 09).
