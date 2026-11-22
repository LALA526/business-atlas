# Business Atlas development

Latest user instruction, 2026-11-22: deliver functionality quickly. Use development → PR → one basic build → Mac8 deployment. Independent Leader/Reviewer/QA calls, repeated integration tests, knowledge manifests and SHA acceptance gates are removed. Older process documents requiring them are superseded.

Develop on Mac16 only. Source ~/Workspace/projects/business-atlas-preflight/env.sh. Use OpenCode/Kimi with configured provider fallback. Preserve existing code; do not restart T02.

Full functional scope remains docs/baselines/T1.0 (44 tasks, 154 requirements, 14 business invariants). Baseline originals are read-only. Use frozen V9.4 UI reference. Implement actual functionality, not placeholders presented as completed features. One requirement task per PR, listing requirement IDs. Preserve real task dependencies and migration ownership. Run only focused checks needed for the change; the pipeline builds and deploys once. No invented approvals or business facts. A deployed framework does not mean the complete product is delivered.

The development pipeline stays limited to Build and DeployMac8. KnowledgeChange/manifest/CI validation may be implemented as product capabilities or examples, but must not be reintroduced as gates on this repository's own development. Preserve this user-directed distinction when implementing T31/T32/release features. Do not restore independent reviews, candidate-build attestations or knowledge-manifest gates to azure-pipelines.yml.
Preview application credentials for actual feature use are private at ~/.config/atlas/product-login.json; read programmatically, never print them or commit them. The actual Self case is this authorized project and its documents/code/PR/deployment history. Do not invent customer facts or human approval. Prepare complete working delivery without waiting for legacy development-process signoffs.
