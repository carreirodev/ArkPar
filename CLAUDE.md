# ArkPar

Fork of MultiPar. A new WinUI 3 GUI (in `app/`) drives the upstream `par2j64.exe` engine, built from `source/par2j`. Read `GLOSSARY.md` before naming anything and `docs/adr/` before changing the architecture.

Linear: project **ArkPar** (P-WPC-15), team **WPC**.

## Upstream files

`source/` and `alpha/` belong to upstream (Yutaka-Sawada/MultiPar). Keep them byte-identical so upstream fixes merge cleanly; change them only for an engine limitation the GUI cannot work around (ADR-0001), and say so first.

## Line endings

Upstream files keep the exact bytes upstream committed (205 CRLF, 15 LF): `.gitattributes` marks them `-text`, which also overrides this machine's `core.autocrlf=true`. The LF + UTF-8 rule applies only to files we own (`app/`, `docs/`, root docs and config). When adding a new top-level folder or root file, add it to the LF list in `.gitattributes`. A blanket `* text=auto eol=lf` would rewrite the upstream files and turn every upstream merge into a conflict.

## tasks.md

Local scratch, ignored by git. Several issues may be built in parallel sessions, so task numbers are global and monotonic: every new task gets the next number after the highest one already in the file (`T1`, `T2`, …), under a heading for its Linear issue. Numbers are never reused or renumbered, including after a task is ticked or removed.

## Agent skills

### Issue tracker

Issues live in Linear, project **ArkPar** (P-WPC-15, team WPC Solutions), via the Linear MCP tools. See `docs/agents/issue-tracker.md`.

### Triage labels

Default vocabulary (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`), team-scoped in WPC; categories map to Linear's `Bug` / `Feature` / `Improvement`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: one `GLOSSARY.md` and `docs/adr/` at the repo root. See `docs/agents/domain.md`.
