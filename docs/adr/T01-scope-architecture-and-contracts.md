# ADR-T01: 文字优先范围、模块化单体与公共合同冻结

- 状态：技术冻结；专用绘图正式替代仍阻断于 G0 业务 Owner 确认
- 输入：`task-dag.json` 原字节 SHA256 `33917a5cbedfb33a5d289bad4d5cd6517bd0691c3523db3b6efb4939254b4ea1`
- 协调器合同输入 digest：`3d30d2e3f8f664930ad2ae17ede998913e1612724dd04ef362dfcf95d3d4422e`
- 批准类型：`delegated-ai`，不是人类签署或生产批准

## 决定

采用一个 ASP.NET Core 模块化单体、一个 PostgreSQL 17 数据库和持久 Worker；React Web 只经 BFF/API 使用生成的 DTO。M01–M12 保持各自 schema 与应用端口，跨模块写入由单一 Orchestrator/UoW 完成，不允许 Domain 引用另一模块 Infrastructure。`database.sql` 只是 T03 生成唯一 EF 迁移链的参考；T01 未运行 PostgreSQL，也未创建迁移。

OIDC Authorization Code + PKCE 与 BFF 安全 Cookie 保护浏览器；API/MCP 使用独立 audience，应用授权与项目 RLS 同时执行。Blob 是私有不可变 hash 对象；PostgreSQL Job/Outbox/Inbox、lease/fence、幂等键及 CAS 保证后续实现的事务与恢复语义。搜索是绑定精确 baseline entry revision 的可重建投影。baseline 是全量不可变 membership，发布、审计和 outbox 必须同事务。

冻结 `contracts/` 下原基线全部字节与相对引用。当前协议为 OpenAPI 3.1、registry `atlas-registry/1.0`、内建 type v1、domain/rule/task-context/impact/agent-result/task-dag v1、KnowledgeChange/Manifest v1.3。AKP、agent-run-events 和 v1.2 只从兼容入口读取，不能覆盖当前设计。合同变化必须同时给出 ADR、差异、迁移评估、消费者更新、版本和兼容测试。

候选原字节/hash 进入 `delivery.proposal_intake`；同一 candidate SHA 经独立 Reviewer、QA 和强制 CI 后才可合并。合并后的 CI 生成绑定真实 merge SHA 的 Manifest。提交、PREPARATION 健康响应和本 ADR 都不等于业务批准、产品实现或部署。

## 范围与需求映射

| 需求 | 文字优先交付语义 | 保留/未批准差异 |
|---|---|---|
| PD-36 / BA-VIS-001 | Flow 用阶段、节点、边、条件与引用的结构化文字表达 | 保留外部图附件和 Flow 语义；不实现专用绘图 |
| PD-37 / BA-VIS-004 | State 用状态、迁移、guard、行为和精确引用表达 | 不保存画布坐标，不引入布局引擎 |
| PD-38 / BA-VIS-007 | 关系通过目录、引用、焦点和可追踪文本导航呈现 | 折叠与焦点只改变呈现，不改变事实 |
| BA-VIS-008 | 受控文本/结构视图和导出保留语义与来源 | 不做热点编辑或图形导出；外部图仍可附件引用 |
| BA-QA-003 | AT-T01 对冻结字节、引用、示例、digest 和篡改拒绝可重复执行 | 静态合同通过不证明业务内容、安全或生产可用 |
| ENG-ARCH-001 | 模块化单体、单 PostgreSQL、Worker、OIDC+BFF、RLS+应用授权 | 架构运行验证归后续 ADR-T01..09 |
| ENG-ARCH-005 | 不可变 baseline、精确 revision、PG Job/Outbox、私有 Blob | T03 独占迁移；备份恢复和性能尚未执行 |
| ENG-DEL-001 | 统一机器合同、版本、相对引用及消费者归属 | 后续生成客户端必须做漂移门禁 |
| ENG-DEL-002 | proposal 绑定任务、repo、候选原字节与 hash | PR 内不写入自引用 headSha |
| ENG-DEL-003 | 独立审查/QA/CI 后由 CI 生成真实 merge Manifest | 当前没有 PR、CI、合并或 Manifest 证据 |
| ENG-DEL-006 | KnowledgeChange 候选记录设计、观察、测试、未知和来源差异 | 未获独立批准，未回写正式知识 |

G0 必须确认上述文字优先正式替代专用绘图。未确认前，范围阻断保留；不得静默恢复画布、热点、布局持久化或图形导出。

## 实际观察与来源差异

本任务直接观察到的只有只读基线、协调器算法和本地静态 lint 结果。基线示例全部是合成/历史结构例；它们不是 CPQ 事实、真实批准、真实 Source 或运行证据。工具/依赖版本、真实 OIDC/MCP 握手、数据库事务、ACL、备份恢复、ARM64 性能与部署均属于后续实测，不在此 ADR 冒充完成。
