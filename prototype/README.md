# Business Atlas V9.4

> Archived working copy only. Do not use V9.4 as the current product design
> reference. Follow `../docs/ui-reference/README.md` and the V9.2/V9.3 design
> direction in the T1.0 baseline.

一个轻量、离线、文字优先的软件项目认知与知识交付原型。

## 运行与开发

```bash
node build.js
node test.js
# 可选：需要本机 Chrome
node scripts/smoke.js
```

打开 `dist/business-atlas-v9.4.html` 即可离线使用。无 npm 运行依赖、无后端、无网络资源。

## 当前能力

- 六项导航：项目概览、知识目录、业务与系统、变更与审核、任务与问题、交付与验收。
- 用文字、条件—判断—结果表和真实案例解释 Feature、Rule、Scenario 与技术对象。
- 七阶段 Flow、条件分支、状态转移和回流以文字阅读，不包含 SVG/Canvas、图形布局、缩放、拖动或 Mermaid/ASCII 图形导出。
- 区分目标规范、当前观察与技术机制；符合性绑定规则、观察、代码、环境和证据修订。
- 简单记录需求；信息不足时只创建待澄清需求。
- 有依据的影响候选、人工复核、业务 Owner 批准/拒绝、DefineRule 草稿、Change Set 发布和 Task 验收。
- AKP/0.1 文件导入、确定性项目助手、反馈、模拟开发交付和完整管理员备份。

## 原型边界

没有真实后端、数据库、企业认证、真实 Agent、仓库调查、测试执行器或部署平台。所有开发 PR、测试和 Manifest 均明确标为模拟。影响候选只基于现有知识，不代表已检查全部代码。

## 当前结构

- `v4-core.js` + `src/v5-core.js`：Schema、实体、断言与基础治理。
- `src/v6-core.js`、`src/v6-projection.js`：Command、Change Set、Baseline 与正式知识契约。
- `src/v7-framework.js`、`src/v8-read.js`、`src/v9-access.js`：分类、Read Context 与授权读取。
- `src/decision.js`：类型化条件、三值判断和案例核对。
- `src/knowledge-reading.js`：业务决策、符合性和 Flow 文字投影。
- `src/task.js`：需求、影响、业务审批、知识发布对账和验收。
- `src/v9-akp.js`：文件导入。
- `src/v9-services.js`：确定性问答、反馈和外部执行记录。
- `src/write-access.js`：统一原型写权限。
- `src/demo-model.js`：语义扩展与演示初始化。
- `src/app-pages.js`、`src/v6-app.js`：文字阅读、Task 与应用入口。
- `fixtures/`：精简当前演示快照、完整 Self AKP 的独立导入入口和小型 Smoke AKP。
- `src/app.css`：唯一样式文件。

`AtlasV93*` 全局名仅为当前内部 API 兼容名，不表示仍加载 V9.3 页面或图形模块。

## 数据兼容

继续使用 `business-atlas-v9` 和 `storageVersion=9`。不会清空现有项目或覆盖用户项目。旧 Visual View 保留为惰性备份数据，不再成为活跃功能；其中注释、来源和引用仍随管理员完整备份保存。

完整 Self Knowledge 位于 `packages/business-atlas-self.akp.json`，通过“导入 AKP”按需读取，不再内联到 HTML。浏览器 localStorage 有容量限制，容量失败会明确提示；大型导入请先备份并考虑使用空白项目。

详见：

- `docs/domain-model.md`
- `docs/task-contract.md`
- `docs/data-compatibility.md`
- `docs/cleanup.md`
- `docs/acceptance.md`

## 当前验证

- 核心语义与工作流：**21/21**。
- 真实 Chrome 离线 Smoke：**13/13**。
- 控制台错误 0，外部请求 0；保留 3 张必要截图。
- 单文件约 869 KiB，21 个脚本、1 份样式；不再内联近 1 MB Self 快照。

结论：适合作为正式产品的高保真交互和领域语义原型，继续受控试点；不代表生产上线就绪。
