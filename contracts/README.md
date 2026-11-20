# T1.0 机器合同实施入口

这些文件是供 Agent 实现/生成客户端/测试使用的候选合同，**不代表已经存在服务或已执行数据库迁移**。先看目录与必要 operationId，禁止将整个大文件反复塞进Agent对话。

| 文件 | 实施用法 |
|---|---|
| `database.sql` | PostgreSQL 17参考物理模式（EF migration唯一事实源：核对该DDL并生成迁移）；真实库执行、角色/权限、扩展、事务与恢复测试才能G1/G2通过。所有project子表RLS；服务层权限仍必须执行。 |
| `openapi.yaml` | OpenAPI 3.1请求响应、错误码、scope、etag、幂等、M12 Job；在CI用支持3.1及外部ref的工具检查并bundle，生成TS client，后端契约测试不能与定义偏离。 |
| `api-catalog.csv` | API索引（operationId、path/method、owner、scope、CAS、CS编号）；开发Agent按自己的模块读取对应操作。 |
| `registry.json` | 内建知识类型、严格publish body schema、原有五维、语义谓词及方向；项目类型扩展受治理；不是导航项清单。 |
| `mcp-tools.json` | Streamable HTTP工具输入/输出JSON Schema及映射，模型不能直接审批/发布/执行shell；工具失败用MCP错误或isError，数据结果遵循对应API envelope。服务端重新授权。 |
| `schemas/domain-v1.schema.json` | 统一可复用 `$defs`，与OpenAPI components对应；草稿RevisionInput允许body不充分但必须显式unknown，批准发布按registry指定严格bodySchema+领域行为校验。 |
| `schemas/rule-v1.schema.json`、`task-context-v1.schema.json`、`impact-v1.schema.json`、`agent-result-v1.schema.json` | 单独可用于外部Agent校验与上下文消费。在应用层补充跨对象与权限约束。 |
| `schemas/task-dag-v1.schema.json` | 本交付候选任务合同，实际任务状态采用持久账本（不是覆写源计划）。 |
| `schemas/akp-v0.1.schema.json`、`agent-run-events-v0.1.schema.json` | 原V9.3协议，保留旧格式兼容；映射说明见08。 |
| `schemas/*v1.2.schema.json` | 原V1.2合同，只作为支持旧输入/事件与旧调度参考，不覆盖当前设计。 |
| `schemas/knowledge-change-proposal-v1.3.schema.json`、`knowledge-delivery-manifest-v1.3.schema.json` | 默认Git CI协议，接受PD编号，移除PR文件内headSha自引用；Manifest用独立CI制品绑定合并提交及候选原字节。 |
| `examples/` | 合成/历史合同例，用于结构测试，不代表生产知识、CI或审批。 |

## 落地约定

1. 复制全部合同至真实仓库 `contracts/`，生成digest；OpenAPI外部schema相对路径须保持（或整体bundle保留身份）。生成TS API client和C# DTO/验证，分层应用端口负责领域授权与不变量。`If-Match`使用被修改聚合version；成员权限操作用project政策聚合版本。已知不透明字段仅允许注册类型、受控内容/证据协议扩展，不允许随意JSON代替领域规则。
2. Native RevisionInput 的 `body` 由 registry 决定语义。Feature、Scenario、Action、Event、State、Constraint、Decision 等按明确body区分；draft结构不足不能作为approved事实。Flow图形不是语义。一般Document、Metric等允许扩展，但必须有业务意义并由对应profile质量检验。任一映射无依据都显示unknown。
3. 每个持久化Source若使用blob须在修订封闭前建立 `intake.source_blob_link`；每个Git proposal持久化 `delivery.proposal_intake` 与原始blob/hash，Manifest必须关联对应proposal并校验task/repo一致性。搜索索引必须绑定准确的baseline entry revision；不能提前写入其他revision。DDL中存在相应约束。
4. 物理DDL未替代业务规则、权限、批准、证据真实性、CAS、幂等、跨修订适用性等应用层检查；测试真实运行。Schema静态例通过不证明领域内容合格，参考例不执行真实生产操作。
5. 候选合同如需修改，必须走技术ADR、合同差异、迁移、消费者更新、版本/兼容测试，审查后统一冻结。禁止两个模块各定义一份同名不同含义字段。

## 浏览器认证控制面（与114项领域操作分开）

T05/T33 实现 `GET /auth/login?returnUrl=...`（仅同源相对路径，OIDC Authorization Code + PKCE、一次性state/nonce，302到IdP）；`GET /signin-oidc`（受框架控制的回调）；`GET /auth/session`（未登录401，登录时仅返回`{principalId,displayName,expiresAt,csrfToken}`，no-store，项目角色通过membership读取）；`POST /auth/logout`（CSRF必需，清本地session，按IdP支持执行logout后受控同源重定向或204）。浏览器会话Cookie使用Secure/HttpOnly/SameSite=Lax，回调由框架管理必要关联Cookie；服务端session密钥持久于私有volume。登出时Web清业务缓存及流。不以session端点返回Bearer token。以上控制面不计入OpenAPI领域资源数量，需独立真实auth端到端测试；实际合同由T01冻结。

每项领域操作的唯一实现Task见 `../03_开发执行/operation-ownership.csv`，其它Task只消费或验证。
