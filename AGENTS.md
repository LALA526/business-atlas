# Business Atlas Mac 开发约定

开发和验证在本机 /Users/fox/Workspace/projects/business-atlas 执行。
工具环境：source /Users/fox/Workspace/projects/business-atlas-preflight/env.sh。
T1.0 输入位于 docs/baselines/T1.0（已冻结的完整副本）。
完整读取其 README、Leader指令、任务所有权及相关机器合同。
当前仅完成环境准备，全部 T01–T44 保持 planned。
按 coordination/tasks.json 的依赖执行；初始同时只执行一个开发任务。
Leader、Developer、IndependentQA 使用不同会话；禁止实现者自行批准其实现。
不要求采用 Multica Squad；采用时也必须保持任务持久化与独立审查。
共享合同归T01、迁移归T03；通过独立分支/worktree修改，禁止抢写。
所有验证在Mac执行，禁止以Windows/Ubuntu执行结果代替Mac证据。
模型使用已有授权的云端CLI；禁止在16GB机器上额外常驻大模型。
产品必须实现原设计的人类审核能力；开发期间可用测试主体验证流程。
AI审核标注为AI审核，不能冒称真实业务Owner或人类最终签署。
真实业务资料、生产身份/域名、外部权限缺失时只阻断相应工作。
不得编造CI、PR、测试、批准、部署、任务完成证据。
不得因为准备环境成功就宣布完整产品交付或G0–G7通过。

本次开发执行器改为OpenCode 1.19.0；角色见opencode.json。Kimi/RoutinAI双路须分别实测后启用。本轮仅准备方案，禁止将角色配置识别成功视为全量开发启动。

正式任务实施前必须读取 coordination/runner/OPERATING_CONTRACT.txt 和 policy.json。初始planned仅描述准备状态，开工后实际进度以SQLite账本、Azure PR/CI与部署证据为准。
