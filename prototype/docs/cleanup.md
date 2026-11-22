# 清理与测试收敛记录

## 基线

Git 检查点 `1207759` 保存 V9.3 全部工作。清理前完整重建和旧测试为 **389/389**。

V9.3：32 个内联 JS、7 份叠加 CSS、约 2.1 MiB HTML、近 1 MB Self 项目快照，同时内联完整 Self AKP。

## 实际删除

- 图形：`v7-flow.js`、`v9-2-reading.js`/`v9-2-ui.js`、`v9-ui.js` 的图形投影与画布、`v9-services.js` 的 graph/Mermaid/ASCII/layout、旧 `stateGraph`、`MoveLayout`、拖动/缩放/专注画布事件及全部画布样式。
- 历史应用：`app.js`、`v4-app.js`、`src/v5-app.js`、旧 views、旧模型/演示语料和不再被依赖的 Engine。
- 历史样式：V3–V9.3 的独立 CSS 文件，合并为 `src/app.css`。
- Self 运行快照：`v9-self-project.js`、`v9-self-package.js` 及预生成脚本；完整 AKP 仅保留为外部文件。
- 历史构建产物、浏览器截图、逐版报告、33 份过程文档和旧测试脚本。
- 当前演示改为约 374 KB 的当前批准快照，不包含其他三个旧行业项目和 Self 历史。

当前按职责保留 `decision.js`、`knowledge-reading.js`、`task.js`、`app-pages.js`、`demo-model.js`，核心复用模块保留稳定 API 名称。

## 测试处理原则

旧 389 项先全部运行并记录，然后停止逐版叠加：

- 图形专项断言随已删除功能移除；Flow 七阶段、14 条边、条件和回流改由文字语义测试保护。
- 只检查旧导航、徽章、DOM 结构和每版截图的重复测试由少量 Smoke 替代。
- 重复的历史治理测试由当前基线、草稿、持久化和 AKP 测试覆盖。
- 权限测试不仅保留，还新增只读禁止创建/候选/发布及 Owner 审批。
- 符合性测试扩展为目标、观察、代码、环境、证据修订失效和缺失绑定。
- 候选无法审批/发布、Task 无法闭合不再作为“已删除旧测试”，而是新增完整工作流测试。

当前唯一日常入口：`node build.js`、`node test.js`；可选 `node scripts/smoke.js`。
