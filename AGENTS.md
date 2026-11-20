# Business Atlas T1.0 · Mac16 开发约定

唯一开发机器：16GB Mac，/Users/fox/Workspace/projects/business-atlas。
工具：source /Users/fox/Workspace/projects/business-atlas-preflight/env.sh。
Multica工作区：567b4222-884b-4e09-9356-e98b78614d23（Business Atlas · Mac16 T1.0）。
唯一执行daemon：01a1958d-ea1d-7167-b939-c21fc53ae637，profile atlas-mac16。
模型：OpenCode 1.19.5 + kimi-code-plan-cn/k3.1-pro；RoutinAI不启用。
不得接续旧Windows空间、APP/ARC issue、旧角色或E盘执行路径。

先读 coordination/runner/OPERATING_CONTRACT.txt 和 policy.json。
权威输入：docs/baselines/T1.0，完整44任务、154需求、14不变量。
基线原字节只读；按DAG依赖和所有权实施，不能缩减为MVP。
UI参考：冻结V9.4文字优先工作副本；V9.3作为历史参考。

Leader仅返回只读计划，由协调器持久化，禁止向工作树写计划。
Builder实现，Reviewer只读审查，QA在独立工作树执行实际测试。
四角色使用独立Multica原生运行，不续接旧会话；不允许角色自行派工。
协调器独占任务状态与集成控制，智能体不能自行标记需求完成。
每个需求交付任务必须有独立、可追溯的PR；PR列出覆盖的需求ID。
禁止以本地提交代替PR，禁止跨无关需求大包合并。
相同candidate SHA通过独立审查、QA及强制CI后才能合并。
合并和真实部署成功后，协调器才能将需求任务标为done。
旧Windows代码未经验收不进入新主线。

T03独占迁移链，公共合同归T01；禁止抢写其他任务所有权。
真实业务事实和外部权限缺失按范围阻塞，禁止编造。
AI批准必须标为AI，不冒充真实业务Owner或人类签名。
准备部署不代表产品实现或G0–G7通过。
实际进度以新Multica任务、私有SQLite账本、PR/CI/部署证据为准。
