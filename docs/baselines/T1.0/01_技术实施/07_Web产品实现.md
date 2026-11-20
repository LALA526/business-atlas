# 07 Web信息架构、交互与实现约定

按P1.0 08/09实现；保留成熟的六入口工作区，而非重造一个管理后台。人的主要注意力用于阅读/审核AI成果、批注和批准，底层关系、状态、引用、调用链由平台和Agent处理。现有V9.2/V9.3仅是视觉/交互参考，不能复制其localStorage运行/权限绕行/旧版堆叠。正式Web使用真实API、认证、持久化、协作。

## 1. 应用与路由

| 路由 | 页面和默认行为 | 关键消费者/API与权限 |
|---|---|---|
| `/projects` | 可访问项目列表、空项目创建、项目入口 | list/createProject；不返回无权项目或计数 |
| `/p/:id/overview` | 简要项目定位/边界、领域能力、系统入口、重要未知/待审；空项目引导导入或提问 | getOverview；ProjectProfile从published snapshot |
| `/p/:id/catalog` | 五维目录+项目领域切换、搜索、可选子类、类型扩展 | getCatalog/listUnits/listSchemas；空类与受限区别不泄露 |
| `/p/:id/business` | 功能/场景和组件两种入口，按项目类型默认；选中功能进入知识阅读 | listUnits/searchKnowledge/buildReadingPlan |
| `/p/:id/knowledge/:unitId` | 知识通用阅读，按profile展示；可链接返回原焦点 | getUnit/buildReadingPlan/evaluateRule；query context锁定基线 |
| `/p/:id/review` `/review/:csId` | 待我审核队列/成果前后对比、评论、决定、发布 | list/getChangeSet, submit/review/reopen/publish；human权限及digest |
| `/p/:id/tasks` `/tasks/:taskId` | 用户自然语言录入，Task目标/确认/未知/下一步，调查进度与实际证据 | createTask/getTask/listIssues/impact/CS/AgentRun；创建权限 |
| `/p/:id/delivery` `/delivery/:taskId` | 各任务目标及保持、实际验证、阻断、业务/代码/知识/部署分轴 | getTaskContext/checkClosure/recordAcceptance/manifest；不可直接改complete |
| 项目顶部菜单 | 项目编辑、成员/角色、schema、资料/AKP导入、历史/版本比较、导出、配置（管理者按权） | 低频次级路径`/settings/...`、`/imports/:id`、`/baselines/:id`，不新增一级入口 |
| 全局右下角浮层 | 项目助手（含上下文问答、反馈、待授权/决策）；移动端抽屉 | conversations/messages/cancel/events; createFeedback/Issue；默认绑定current root+revision |

统一`ProjectShell`（左6入口+顶部项目切换、搜索、版本模式、用户角色标识；不提供任意模拟身份下拉）、`ReadContextProvider`（published默认、historical需准确B、draft仅变更集）、`ApiClientProvider`。樊篱：只消费生成的TS客户端，context/ETag/错误统一拦截；route params不可作为授权凭证。浏览器登录cookie httpOnly，写API使用防CSRF token，跨域默认拒绝，MCP token不进入浏览器。前端权限只决定呈现和操作按钮，服务端任何动作重新授权。关闭图形、可视化画布、Mermaid、关系图、图导出及复杂表单，不引入对应路由/依赖。

## 2. 页面组件职责

`ProjectOverview, CatalogBrowser, GlobalSearch, VocabularyDetail, KnowledgeReader, KnowledgeEditor, RuleTable, CaseEvaluator, FlowTextReader, DesignSection, SourceDrawer, RevisionSelector, CitationLink, ReviewQueue, ChangeReview, InlineComment, DecisionDialog, TaskCreate, TaskDetail, ImpactSummary, EvidenceDrawer, DeliverySummary, AssistantDrawer, ImportWizard, PermissionSettings, AuditView`。组件不要求逐个单独文件，根据职责内聚；禁止为了体现概念全建空页面。

`KnowledgeReader`拿`ReadingView.sections`而非raw entities。顶部标题、业务目的、重要版本/冲突；Feature默认三个Tab：
1. **业务需求**：背景/需要的结果、使用者、范围、成功标准；具体业务判断由规则Tab承载，不写SQL/技术方案。
2. **业务规则**（默认重点）：短文或条件—判断—结果表；每列不同信息，有具体字段语义、默认/异常、关键正反/边界例。没有正式目标时显示未知/候选而非根据现状生成。复杂Flow用阶段、进入条件、操作、规则、结果、后继/回流的文字可展开；保留节点/边引用。CaseEvaluator可录入实际业务数据、调用规则求值，显示matched/unknown/conflict和原因；不当真实业务执行。
3. **业务设计**：分离已批准设计、当前观察行为、技术实现机制、实际差异；点组件/接口/字段深入。设计未确定显示候选，不能把代码映射当业务目标。

其它必要内容（参与者、状态、资料、测试、运维）由profile和用户工作目的按需展开，不强制所有type有三Tab。DomainTerm页：别名、context、定义、例、规则使用、业务属性到字段映射；Component/API页：职责、输入输出、契约、数据和异常，优先技术用途。源定位drawer显示授权excerpt或source metadata、precise revision和locator，可跳转/下载；受限来源不给敏感标识，必要语义不足显示partial。History切换保留当前阅读焦点，浏览器back恢复Tab/anchor/scroll。导出使用server授权内容，当前时间戳、baseline/context完整；禁止图像/布局字段导出冒充事实。

## 3. 编辑与审核是真正的核心交互

`KnowledgeEditor`读取profile和目标base revision，默认自然语言编辑/粘贴来源、AI组织为候选，显示可读内容而不是JSON/大表单。对于可执行Rule，结构化条件输入和高级表达编辑器为可选深入；`ReviewContent`显示行级缺少依据、比较语义、混写和冲突。无法确定时保存草稿，不能标规范已完成。提交CS是具体差异+必要影响和证据。保存操作需要Idempotency-Key及If-Match；409/412显示版本冲突、前后差异、重试或基于当前重提交，不覆盖他人工作。用户不用自己维护全部知识关系；AI proposal与手工编辑最终同一命令/审批。

`ChangeReview`默认呈现：本次需要解决什么、候选内容（同三Tab）、差异、保持项、已确认影响、需要人决策的未知和推荐、必要证据、当前审批责任。逐项批注绑定submitted revision/block/row anchor/quote hash。用户可“要求修改”“拒绝”“批准明确范围”，批准前确认被批准的内容及必要依赖组、版本digest、风险和作用域；审批操作进入审批API，不能直接改本地status。复审显示旧意见是否响应及实际差异，不重新把全部不变正文展开。审批结果与发布分开，有权发布者可见“批准待发布”，发布前重检查未过期base。未授权按钮不可见/禁用有理由；直接调用后端仍须拒绝。review queue按实际role及gate，不展示所有内部状态标签。审批成功后从published API读取新事实，不用optimistically覆盖旧B。

## 4. Task/Delivery（轻量需求入口）

Task创建表单默认只有一段真实意图、可选关联功能/附件；空值不提交，不预填“调整某某条件”等假规范。提交后显示实际调查状态，没有真实Agent则明确等待或只有候选。详情默认四块：想改变什么（As-Is/To-Be、保持）、已确认结论、需要关注（真正要人决策的未知/冲突及方案）、下一步与交付状态。下层可按需展开影响路径、调查证据/覆盖、实际Agent run、任务上下文、测试、PR、知识修订与对账。不能自动生成15类空调查表；待查重要问题聚合为业务结果和建议，用户可评论并批准决议。影响记录的required/potential/no_change/deferred/unknown和实际调查状态清楚区分；任何关键未知就近呈现。

Delivery页把本次目标与保持项、实际验证、未处理影响、审批/知识发布、部署code版本、知识债放在最前；打开证据可以到CI artifact/版本锚且权限重验。`CheckClosure`给阻断原因，要验收时再次由server核查；模拟数据只能进入隔离EX演示，不存在生产mock“完成任务”按钮。代码已合并、待知识审核时显式状态，不因用户希望完成而自动批准。问题分流、反馈到CS，可在相应知识引用回溯其决策和变更。

## 5. 异步、多人协作与前端状态

TanStack Query key包含projectId, baselineId, changeSetId, contextHash, principal session, policyEpoch；禁止跨角色复用React持久cache，登出/切项目/撤权清理。列表采用cursor分页，维持选择/焦点；404不推断隐藏对象名称；所有加载/无数据/错误/权限不足/依赖未配置/partial状态有明确可行动提示。BFF Cookie刷新与401统一恢复，不能自动以管理员重试。HTTP mutation pending防重复；请求重试复用同key，真正修改生成新key。低风险本地编辑可暂存草稿（非敏感），默认server draft持久；刷新后从server获取状态；localStorage仅UI偏好，不保存权威知识、token或敏感响应。

SignalR Project hub：已认证project订阅、server加入组前授权，事件只含安全event kind和opaque refresh hint，不推送敏感正文、总数量或来源名；接收后重授权查询，不以通知当事实。两个用户编辑同一候选CAS拒绝后提供三方差异；评论与任务进展实时刷新。断线重连重新授权、使用cursor补活动；若role被撤，立即停止订阅/流且清缓存。不同浏览器用户实际操作的自动化E2E是正式产品必须，不以两个模拟role选择框代替。

助手右下轻量入口绑定当前root+baseline，利用CS-02/03/04；明确回答中的事实、推断、未知和引用。真实LLM流按服务响应，取消和失败可恢复；模型输出Markdown净化，不渲染任意HTML/危险链接。用户提出改变时形成候选或反馈，不直接写规范。限制可见资料、Source下载和外部模型传输。

## 6. 验收视觉和交互标准

桌面1440/1280、1024 tablet及375 mobile检查六入口导航/抽屉、阅读与审批、表格局部横向滚动但整页不溢出。可键盘操作、焦点可见、Tab ARIA、label、modal焦点恢复、对比度及非仅色彩状态，正文中文易读（不要极小字号/英文技术眉题/堆叠badge）；出错提示靠近相关内容。无默认铺开所有知识维度、不会重复同一摘要和判断、无大型图形工作台。每页实际工作路径可从有意义的信息出发，不需内部ID。用真人黄金任务判断内容是否真正可用，不以截图好看来代替认知验收。正式Web测试数不机械累加原型389断言；关键行为、负例、并发、权限和真实旅程保留，必要的unit/integration/Playwright测试按风险设计。
