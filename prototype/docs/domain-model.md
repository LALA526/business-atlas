# 当前领域模型

## 三类事实

- **目标业务规范**：已批准 Rule 版本，说明业务应该怎样处理。
- **当前观察行为**：ObservedBehavior，绑定观察对象、版本、环境、输入、实际输出、方式和可信度。
- **技术实现机制**：API、Component、DataEntity、Configuration、Event 等现有实体，说明怎样实现，不自动定义业务目标。

## 业务判断

Rule 使用 Context、Inputs、Preconditions、Evaluation、Priority、Branch、Outcome、Default、Exception、Evidence。条件支持 equals、notEquals、contains、startsWith、exists、notExists、greaterThan、lessThan、in、all、any。

条件采用三值逻辑：成立、不成立、未知；不支持的条件单独返回 unsupported。缺失必要输入不默认成功，多规则冲突不默认第一条/最新/最窄优先。案例固定规则版本，只解释和验证，不反推规范。

## 符合性

符合性绑定：目标规则 ID/版本/修订、观察 ID/修订、代码版本、环境、证据 ID/修订、确认人及比较记录。任一依据变化即待复核；绑定不完整不能沿用旧 conformant 标签。支持时执行确定性案例比对，否则保留人工确认及范围限制。

## Flow 与状态

Flow、Node、Edge、阶段、条件、异常和回流继续保存和参与授权投影。默认显示阶段摘要，展开当前阶段后查看动作、判断、结果、下一阶段、异常和回退。没有 SVG、坐标布局或图形生成。

状态与状态转移用列表和表格表达；未确认转移不成为正式确定事实。

## 知识与版本

Entity、Assertion、Source、Command、Change Set、Baseline、Read Context 和 Knowledge Access Boundary 不变。历史基线不可变，草稿与批准视图分离，Flow 引用仍按权限和版本解析。
