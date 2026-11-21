# Business Atlas development

Latest user instruction, 2026-11-22: deliver functionality quickly. Use development → PR → one basic build → Mac8 deployment. Independent Leader/Reviewer/QA calls, repeated integration tests, knowledge manifests and SHA acceptance gates are removed. Older process documents requiring them are superseded.

Develop on Mac16 only. Source ~/Workspace/projects/business-atlas-preflight/env.sh. Use OpenCode/Kimi with configured provider fallback. Preserve existing code; do not restart T02.

Full functional scope remains docs/baselines/T1.0 (44 tasks, 154 requirements, 14 business invariants). Baseline originals are read-only. Use frozen V9.4 UI reference. Implement actual functionality, not placeholders presented as completed features. One requirement task per PR, listing requirement IDs. Preserve real task dependencies and migration ownership. Run only focused checks needed for the change; the pipeline builds and deploys once. No invented approvals or business facts. A deployed framework does not mean the complete product is delivered.
