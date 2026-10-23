# AGENTS.md

- A comment is either API XML doc (consumer semantics) or a note on special /
  intent-obscuring usage; nothing else.
- Comments and docs are not logs: no implementation, testing or experiment
  records; no batch/stage bookkeeping.
- No invented numbering (D2, P3b, fix-3); no quoting conversation content.
- The repository language is English. Commit messages follow the same rules.
- Prism compatibility is read-and-migrate only: Prism files are read and
  migrated in; migrated data uses our own formats and layout. No Prism write
  compatibility, no Prism layout is kept.
