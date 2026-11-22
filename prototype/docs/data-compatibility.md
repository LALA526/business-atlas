# 数据兼容与迁移

- 沿用 `business-atlas-v9` localStorage 和 `storageVersion=9`，初始化不会清空存储。
- 旧项目、Entity、Assertion、Rule、Flow、Task、Baseline、Change Set、来源、导入记录、反馈、Agent Run 和草稿继续读取。
- 新演示种子只在项目 ID 不存在时补充，不覆盖用户同名项目。
- 旧 Task 缺少当前语义时按需投影为未分析，不自动补齐目标或影响。
- 旧 Visual View 不再激活；布局、坐标和缩放不进入当前页面。注释、来源、引用及原始对象仍保存在项目数据和管理员完整备份中。
- 完整 Self Knowledge 只以 `packages/business-atlas-self.akp.json` 提供，用户通过文件选择器导入；HTML 不内联完整 AKP 或预生成 Self 项目。
- 小型 `fixtures/import-smoke.akp.json` 仅用于浏览器导入 Smoke。

AKP 未声明有效期时使用无时间边界，不擅自把导入日作为业务生效日。严格版本读取不回退到 draft/current。

localStorage 有浏览器配额和清理风险，容量失败必须显式提示。大型 Self AKP 与多个成熟项目同时存储可能达到配额；请先完整备份，再使用空白项目或减少历史执行日志。原型不提供服务端迁移、并发或在线回滚。
