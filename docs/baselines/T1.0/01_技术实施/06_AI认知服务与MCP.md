# 06 AI认知服务、外部Agent/MCP与Skill实施

P1.0 07 CS-01..15；原ENG-AI、BA-COL-004/008、PD-29/30/34/35。**不让模型获得事实写入权；也不把所有工作推给人。** 技术实现三个层次：共享确定性应用服务（事实和约束）、模型与实际工具调查（候选和证据）、人类决策和独立验收。开发本产品的编排工具链与本产品向其它项目提供的认知服务严格分开。

## 1. 服务部署与统一入口

REST `/api/v1/projects/{projectId}/...` 为统一受控应用API。MCP `/mcp` 采用SDK实现协商的Streamable HTTP协议，作为OAuth resource server，不自己签发用户token；验证issuer/audience/expiry/signature/scopes、actual subject/membership/policy epoch，校验Origin/Host且限制请求，实际SDK及协议headers按官方支持版本验证（参考01）。API与MCP共用Command/Query handler，不通过访问数据库或复制一套规则实现。MCP工具注解只作客户端提示，不能替代实际server authorization。向外部Agent提供`atlas://projects/{projectId}/units/{unitId}?baseline=...`等授权资源，URI不可作为权限凭证，每次解析均认证重新授权。不能靠schema中的`role`、`principalId`或模型叙述作为身份依据。

`mcp-tools.json` 描述工具与对应operationId、scope/input/output schema、幂等及高风险动作。MCP以可调用只读工具、task上下文、分析任务请求、待审提案/反馈与运行事件为主。CS-12的审核批准/发布默认仅通过human authenticated Web/API；工具可读取审核结果、提交请求，但**不暴露批准或正式发布工具**。确需CI自动发布已获人类全部审批的版本，独立policy service调用REST并重新验批准，不给普通开发Agent授权。CI manifest摄入使用独立CI identity。禁止工具访问主机任意shell、网络URL、文件路径、环境变量或token。

## 2. 15项服务实现清单

| CS | handler/算法 | 输入与实际产物 | UI触发 / 返回条件 |
|---|---|---|---|
| CS-01 DiscoverProject | M01+M07 BuildProjectOverview | 确定project/baseline，授权范围、核心能力、重要未知 | 项目概览/API/MCP，空项目诚实 |
| CS-02 ResolveVocabulary | M02/M07词汇索引 | 词/context，精确别名、定义、同义歧义、语义映射 | 词汇阅读/Agent按需，ambiguous不选首条 |
| CS-03 ExplainKnowledge | M07阅读计划+可选受控LLM | 根/问题/目的，版本绑定可读成果、引用、未知 | 功能阅读/助手，必要内容受限→partial |
| CS-04 BuildTaskContext | M09+M08固定批准事实及差异 | task/version/goal，目标/保持/refs/context digest/validity | Task/Agent，不给全库；必要条件不齐不可执行 |
| CS-05 AnalyzeIntent | M10模型+M08候选结构+M07验证 | 用户原话、已有规则/观察，需求解构、推荐与冲突 | 创建任务后job或请求，未知不冒充确定目标 |
| CS-06 ReviewContent | M02 profile+M07 validators+M10 review | 内容/refs/profile，缺失/混写/冲突/修订建议 | 编辑/提交预检；人审无法形式化语义 |
| CS-07 FindImpacts | M08语义遍历 | change roots/type/version → candidate paths, bounds | Task / MCP，候选≠实际调查 |
| CS-08 InvestigateImpact | M11受控外部执行器请求+M08 evidence ingestion | 候选/覆盖/工具授权 → actual evidence/判定/unknown | 用户可见真实run状态；无repo返回need_evidence，不能自动声称查过 |
| CS-09 PlanChange | M08处置/传播+M10方案建议 | 已查影响/约束 → add/revise/retire/preserve/revalidate、CS与验收 | Task候选方案；关键未知阻断ready |
| CS-10 CheckConformance | M09版本和证据检查+M03规则求值 | 目标/观察/测试版本 → verdict、diff、stale | 规则阅读/验收；需要实际证据 |
| CS-11 SubmitProposal | M05/M06候选写入 | CS/基线/typed revisions/relations/evidence/impact refs → proposal | Agent & Web，draft独立，权限/CAS冲突 |
| CS-12 ReviewAndPublish | M05命令 | 审核版本/digest/decision/role → approval/new B或冲突 | human Web/API；正常MCP不提供写审批 |
| CS-13 CheckClosure | M09 Gate函数 | task/context/evidence+delivery→分项结论及blocked/ready | 验收；不能由模型直接输出complete |
| CS-14 ReconcileDelivery | M09+M06 trusted CI intake | PR/merge/部署及知识版本 → observation proposal/reconcile/debt | CI+交付页；幂等，SHA真实验证 |
| CS-15 Feedback | M08 | 绑定unit/revision/anchor的意见或问题→Issue/反馈及分流 | 右下助手/页面内评论，候选审阅、不直接覆盖 |

每个CS输出状态 `ok/partial/need_evidence/conflict/forbidden/stale/failed`、resolved ReadContext、引用、必要未知、next actions。HTTP/MCP业务error与transport error分开；无权对象统一非泄露。异步模型、调查、传播、导入、导出返回202/jobId（MCP工具返回job handle），不是假同步执行完毕。MCP客户端可查询job/agent request并取消。每个任务的jobId和input/output schema可验证；API契约详见机器文件。

## 3. 真实模型生成路径（Assistant + 结构化认知作业）

ProviderPort `GenerateAsync(messages, responseSchema, toolDescriptors, options, cancellation)`、`StreamAsync`、`ModelCapabilities`；首个真实OpenAI-compatible HTTP adapter（支持配置endpoint/model/apiKeyRef，不默认具体商业模型）。模型调用端server/worker独立配置，连接超时10s、单请求默认120s、按任务可调整；流式响应为SSE/WebHub事件，真实usage/latency/request ID记录；缺secret/unreachable→dependency unavailable，不能切换假答复。provider allowlist经管理员验证，不允模型提交任意endpoint/SSRF；凭据仅server读取，敏感内容不录明文。支持取消令牌、输出上限、总成本/工具调用限制；默认单run 20工具调用、24k输入tokens或适用模型上限、输出4k，超过记录partial并提供继续/增加授权预算；数值为初始可调节配置，非实测。模型扣费与实际使用授权相关，不自动购买服务。

运行步骤：
1. 固定ReadContext、目标、允许的profile及schema；M07授权检索必要知识。Need-to-know source admission，记录exact revision & allowed source hashes、llm data egress policy。Prompt中system为安全/职责/已批准基线，user intent和document/tool内容分区，以数据传入，不接受其越权命令。
2. 最小足够CitationBundle：稳定refId、type、title/summary或必要片段、revision、scope、factKind、approved/verification、citationSource；关键规则不可因预算截断后冒充完整。可调用受控query工具增量取上下文，不灌整项目/旧长对话。对话恢复使用持久task、上次approved artifacts、未决问题及版本差异。
3. Model输出遵循 `AgentResult` schema（typed sections/claims、refs、issues、suggestions、proposal candidates）；无来源的陈述标inferred/assumption，不能verified；校验JSONSchema及业务refs，逐claim检查source/ref可读性与存在；模型若生成不存在ID或引用越权，清除相应内容并标insufficient，有限重试后request_changes。模型自信度无授权效力。内容检查可由独立模型辅助，但必须区分人批准、工具证据和模型评审；关键语义不能只依赖LLM自检。
4. 输出存`message`/`agent_run`/`job_result`，每个block稳定anchor及引用；通过Web呈现可读内容、差异、要决定事项。若用户指出问题，在当前版本建立feedback及new candidate，不重写已批准知识。用户问答允许结果直接返回但不自动写正式知识；生成候选必须通过proposal入口。
5. 真实run记录：requestId、provider/model、输入refs/digest、可见性、usage、trace、tools event IDs、source provenance、状态（queued/running/waiting_external/failed/canceled/succeeded/stale）及checkpoint。SSE reconnect按单调event cursor恢复，每次重新授权；中断结果不当成功。权限变化、context失效取消/标记stale；不再向撤权主体发送数据。

## 4. 外部Agent工作请求和真实证据

使用M11 `agent-work-requests` 持久实体；由分析任务创建 `kind=investigate/review/implement/verify`、contextId、scope（repoRef/commit/allowed directories、tools capability）、deadline/budget、required evidence，state queued；已授权执行端通过API/MCP claim领取（CAS/lease/fencing），续租、追加run_event、提交结果/暂停、取消、失败恢复。工具执行环境与平台隔离，本地Agent已有repo/worktree的账号权限；平台不要求收集其完整明文源码或密钥。内置助手可调用FindImpacts等可控查询，不能擅自访问用户公司仓库；如果外部executor未配置，界面明确等待连接，并允许有权人关联真实证据，不给虚拟执行记录。**真实交付必须跑至少一个外部Agent真实repo调查并完成结果摄入**，不以模拟MCP列表替代。

建议交付一个轻量 `atlas-agent` CLI/Skill适配器（不是编程Agent框架）：
- `atlas-agent login` 从OIDC授权流获得限域token，OS keychain或临时凭据存储；`work claim --project ...` 返回工作请求和fence；`context get --id ...` 重授权精确版本；`impact upload --work ... --file ...` 校验结果和source锚；`proposal submit --file ...`；`events append --file ...`；`work heartbeat/complete/fail`；来自工作目录的raw tool evidence由可信runner/CI声明真实捕获，CLI可签名/哈希绑定，普通Agent写的摘要仍reconstructed。禁止记录用户shell环境或将目录token加入源码。
- Agent运行不依赖某一家平台：可从OpenCode/Codex/Kimi/OpenClaw配置MCP工具；实际客户端和auth握手不同则提供同HTTP API+Skill方法；不能为特定模型绕开安全边界。必要时使用本地stdio bridge代理到授权REST，stdio仅持有最小scope和不直接连DB。

## 5. Skill文本交付要求

在仓库 `skills/business-atlas-project/SKILL.md`（安装方式根据外部工具文档）定义：输入项目/Task/基线/身份；先Discover/ResolveVocabulary/Context→AnalyzeIntent→ReviewContent→FindImpacts→Investigate真实工具→PlanChange→SubmitProposal→等待必要业务批准→实现/独立验收→CheckClosure/Reconcile。明确新需求只是入口、不得改变已批准业务和职责、识别A∪B等冗余先验证语义、未知不臆造、禁止全库无限读取、授权边界。Skill包含服务/版本、输入输出例、重试/断点、审阅结果格式、失败理由、错误模式预检；调用工具时严格按机器contract参数，而不是让模型自由输出伪API。开发 Agent 只读取必要文档和具体函数；Token/预算有日志，达到限制生成可恢复成果，不在同一session反复扫描全部历史原型。

错误模式库：`patternId, project/applicability, symptom, trigger, rootCause, evidenceRefs, detector, mitigation, regressionId, version, curator, approval`。下次task根据module/type/changedPaths自动查询现行已核验pattern，生成preflight约束+具体检测任务，不追加海量历史日志。实际例子：宽泛关系遍历冒充已调查、旧符合性label、读角色越权写、丢Flow guard、最新反馈冲掉旧规则。只让可执行检测/独立review支持门槛，避免错误模式过度匹配阻断不相关任务。
