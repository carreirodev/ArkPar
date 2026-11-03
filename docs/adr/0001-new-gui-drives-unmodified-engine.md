# New GUI drives the unmodified upstream engine as a separate process

MultiPar's GUI (`MultiPar.exe`) is closed source and 32-bit only, so ArkPar replaces it with a new GUI. The new GUI runs the upstream `par2j64.exe` as a child process, reads its progress from stdout and its result from the `/w` JSON file, and sends it the cancel, pause and resume keystrokes through a hidden console. We leave `source/par2j` untouched so that upstream fixes merge cleanly, and change it only for a limitation the GUI cannot work around.

## Considered Options

- **Change the engine to add a better IPC channel** (structured progress, a cancel pipe). Rejected for now, because every upstream merge would need manual reconciliation.
- **Port the engine into the GUI as a library.** Rejected, because the GUI would have to be GPL-linked and we would carry the whole C codebase as our own.

## Consequences

- The GUI depends on the engine's text output format. A change upstream can break parsing, so tests use captured real output.
- PAR3 support waits for a stable upstream release, as PAR3 does today.
