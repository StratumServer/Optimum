---
name: codex-handoff
description: Hand a stuck rendering bug or a plan review to the local Codex CLI (gpt-6-astra) with a neutral brief, full machine access, detached launch and a completion monitor; then read its report and transcript. Use when the user says "give it to codex/astra" or after two failed fix attempts.
---

# Codex handoff

Brief = symptom + reproduction + where things live. No theories, no ruled-out lists; that poisons it.

1. Stop other writers: pause workflows/agents, commit WIP (`wip:` prefix, never stash), clean tree.
2. Write `<scratchpad>/codex-<topic>-brief.md`: repo path and branch; the user's words verbatim;
   screenshot paths; how to build (`make deploy`), launch (`scripts/dev/run-client.sh`), stop, switch
   renderer, read the renderer log line; diagnostics env vars; the test commands; the patch workflow
   rule (edit build/ + fork, run extract, never patches/sources); ask for a report file and a commit
   on the branch, no push.
3. Wrapper script (the tool timeout cannot kill it):
   ```
   cat brief.md | codex exec -m gpt-6-astra -c model_reasoning_effort="high" \
     --dangerously-bypass-approvals-and-sandbox -i shot1.png -i shot2.png > codex.log 2>&1
   echo "CODEX_EXIT $?" >> codex.log
   ```
   `setsid wrapper.sh &` then a Monitor that greps for `CODEX_EXIT`. Effort: `high` for reviews and
   rendering bugs (~15-40 min), `low` for small tasks; it is on a weekly quota.
4. When done: read the report, `git log`, and the transcript
   `~/.codex/sessions/<date>/rollout-*<session id>.jsonl` (condense `response_item` messages +
   `custom_tool_call` inputs). Verify its claims yourself in-game before relaying them.
