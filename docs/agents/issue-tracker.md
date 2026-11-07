# Issue tracker: Linear

Issues and specs for this repo live in Linear: team **WPC Solutions** (key `WPC`), project **ArkPar** (`P-WPC-15`). Use the Linear MCP tools for every operation. Their prefix depends on how Linear is connected (currently `mcp__plugin_linear_linear__*`), so look for the operation names used below (`save_issue`, `list_issues`, `get_issue`, `save_comment`, `list_comments`) rather than a fixed prefix. Code and pull requests live on GitHub (`carreirodev/ArkPar`); `WPC-42` is a Linear issue, `#42` a GitHub PR.

The `WPC` team is shared with other projects, so always scope lists and searches to `project: "ArkPar"`.

## Conventions

- **Read the spec**: the project description is the spec (scope, order, parallelism). `get_project` with `query: "P-WPC-15"` and `includeMilestones: true`. Milestones are the phases (`E1 · Verificar e reparar`, `E2 · Criar`, `E3 · Lote e acabamento`).
- **Create an issue**: `save_issue` with `team: "WPC Solutions"`, `project: "ArkPar"`, `state: "Backlog"` (or `"Todo"` for a ticket whose plan is complete when it is created, such as one `/to-tickets` cuts from an approved spec), a `title`, a Markdown `description`, a `milestone` when it belongs to a phase, and an `estimate` on the T-shirt scale sent as a number (XS = 1, S = 2, M = 3, L = 5, XL = 8). Search first with `list_issues` (`project: "ArkPar"`, `query: "..."`) so you don't file a duplicate.
- **Read an issue**: `get_issue` with the identifier (e.g. `WPC-374`) and `includeRelations: true`, so blocking edges are visible. Comments: `list_comments` with the same `issueId`.
- **List issues**: `list_issues` with `project: "ArkPar"` plus `state`, `label`, `milestone` or `parentId` filters.
- **Make an issue a sub-issue of a parent**: `save_issue` with `parentId: "WPC-<n>"`, on create or afterwards.
- **Start work on an issue**: `save_issue` with `assignee: "me"` and `state: "In Progress"` before the first code edit. Work on the branch Linear suggests (`gitBranchName` from `get_issue`), never on `main`, and put the identifier in every commit message (e.g. `WPC-374: build par2j64 from source`) so `/code-review` can find the ticket.
- **Record a decision**: each decision or plan change made during a build goes in as a `save_comment` on the issue. Rewrite the description only after re-reading it, so an edit the owner made in Linear is never overwritten.
- **Comment on an issue**: `save_comment` on the issue.
- **Apply / remove labels**: `save_issue` with `addLabels` / `removeLabels`, never `labels` (it replaces the whole set). Create a missing label with `create_issue_label` first.
- **Close**: when verification starts, `state: "In Review"`. Re-read the description and update it so the acceptance criteria say what was actually decided, ticking the ones met. Then post one result comment that lists each criterion with its proof (a named test, a command and its output, a live check) or "não verificado" and why. When verification passed and nothing is left waiting on the owner, `state: "Done"`; a failed check sends it back to `"In Progress"`. For work that won't be built, comment the reason, then `state: "Canceled"`.

## Pull requests as a triage surface

**PRs as a request surface: no.** _(Set to `yes` if this repo treats external PRs as feature requests; `/triage` reads this flag.)_

## When a skill says "publish to the issue tracker"

Create a Linear issue in project ArkPar, as in **Create an issue** above.

## When a skill says "fetch the relevant ticket"

Read it as in **Read an issue** above.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a single issue with **child** issues as tickets.

- **Map**: a single issue in project ArkPar labelled `wayfinder:map`, holding the Notes / Decisions-so-far / Fog body.
- **Child ticket**: a sub-issue of the map (see **Make an issue a sub-issue of a parent**), labelled `wayfinder:<type>` (`research`/`prototype`/`grilling`/`task`). Once claimed, the ticket is assigned to the driving dev and In Progress.
- **Blocking**: Linear's native issue relations, the canonical, UI-visible representation. Add an edge with `save_issue` on the child, passing `blockedBy: ["WPC-<n>"]` (it appends; `removeBlockedBy` removes). A ticket is unblocked when every blocker is in a completed or canceled state.
- **Frontier query**: `list_issues` with `parentId: "<map>"`, open states only; drop any with an open blocker (`get_issue` with `includeRelations: true`) or an assignee; first in map order wins.
- **Claim**: `save_issue` with `assignee: "me"` and `state: "In Progress"`, the session's first write (see **Start work on an issue**).
- **Resolve**: `save_comment` with the answer, then `save_issue` with `state: "Done"`, then append a context pointer (gist + link) to the map's Decisions-so-far with a `patch` on the map's description, never resending the whole description.
