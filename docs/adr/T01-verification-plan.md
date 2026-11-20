# ADR-T01 验证计划与证据边界

## 本任务实际执行（AT-T01）

1. 解析 OpenAPI 3.1、17 份 JSON Schema、registry、MCP 映射、CSV 索引及全部示例；检查本地/跨文件引用。
2. 对照 operationId、HTTP method/path、owner 和 scope，拒绝重复 operationId/type/predicate/tool。
3. 对 `contracts/` 与只读基线逐字节比较；manifest 按排序 POSIX 相对路径和内容 SHA256 计算 tree digest。缺失、额外、字节变化均失败。
4. 校验冻结 JSON 的真实 DAG digest、协调器 input contract digest、14 个需求、完整 44/154/14 基线、显式 unknowns 和 `delegated-ai`。
5. 自动测试覆盖正向、损坏/篡改拒绝、并行重复 digest 一致性和中断临时文件后的 manifest 重建。

命令：

```sh
/Users/fox/Workspace/projects/business-atlas-preflight/.venv/bin/python scripts/ci/verify_task.py T01
/Users/fox/Workspace/projects/business-atlas-preflight/.venv/bin/python -m unittest discover -s tests/Contract -v
```

## 后续真实实现门禁（本任务未执行）

- 正向：OpenAPI 生成 C# DTO/TypeScript client 无漂移；真实项目隔离、OIDC、MCP、发布、Source Blob、精确 revision 投影、备份恢复和端到端业务流程。
- 拒绝：raw HTML、未知/循环 ref、approved 草稿缺 strict body、显式 unknown 缺失、跨项目引用、错误 audience、外站/绝对 returnUrl、state/nonce 重放、无 CSRF logout、公开 Blob、Manifest task/repo/hash 不符、模型直接审批/发布/shell。
- 并发：聚合 CAS；同幂等键不同 request digest 返回 409；project→change_set→稳定 unit 顺序加锁；同基线并发发布仅一个成功；全量 baseline entry 无重复 unit；Worker `SKIP LOCKED` 租约竞争、fence 拒绝过期持有者、重复事件和撤权后重授权。
- 恢复：kill Worker、网络超时、导入中断和发布事务故障后无半发布、重复对象、丢失 outbox 或旧 revision 改写。迁移遵循 backup/dry-run/expand/backfill/verify/contract，DB 与 Blob 一致备份并完成不可逆变更回滚演练。
- ADR-T01..09 分别验证架构依赖、baseline 规模、双项目授权、真实 Repo 影响调查、多语言检索、Worker 故障、Blob 恢复、ARM64 资源和真实模型/MCP。

静态 lint 和合成 examples 通过只说明合同结构一致，不能替代上述实际领域、安全、性能、恢复或部署验收。
