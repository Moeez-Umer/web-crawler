---
trigger: always_on
---

# Project rules (always apply)

1. Source of truth is docs/SPEC.md. Read it, plus docs/Decisions.md, docs/Flow.md and docs/PROGRESS.md, at the start of every session.
2. Work on ONE phase at a time, the one I name. Never start the next phase.
3. Before coding, show a short plan (max 10 bullets) listing the files you will create or change. Wait for my "go".
4. A phase is done only when: it builds, its tests pass, and you have pasted the command output.
5. At the end of every phase you MUST:
   a. Append every non-obvious choice to docs/Decisions.md using the template in that file.
   b. Update docs/Flow.md so it describes only what is actually implemented. Mark anything not yet built as [PLANNED].
   c. Update docs/PROGRESS.md (phase status, open issues, next step).
   d. Give me a 5-line summary and a list of changed files.
6. If something is ambiguous, pick the simplest option, record it in Decisions.md, and flag it in your summary. Do not ask me mid-phase unless blocked.
7. Do not add features outside SPEC.md. Keep classes small, no empty catch blocks, no hardcoded secrets.
8. Add comments only where the WHY is non-obvious (idempotency, completion counter, tree building).