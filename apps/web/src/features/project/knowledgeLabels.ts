const kindLabels: Record<string, string> = {
  ProjectProfile: '项目定位',
  Feature: '业务功能',
  BusinessRule: '业务规则',
  Rule: '业务规则',
  Scenario: '业务场景',
  Flow: '业务流程',
  DomainTerm: '领域术语',
  DomainConcept: '业务概念',
  BusinessAttribute: '业务属性',
  Component: '系统组件',
  Api: '接口契约',
  ApiOperation: '接口操作',
  Requirement: '产品要求',
  Decision: '项目决策',
  Observation: '实际观察',
  Design: '业务设计',
  Architecture: '技术架构',
  Evidence: '依据与证据',
  Implementation: '实现记录',
  Task: '工作任务',
  Milestone: '交付阶段',
};

const factLabels: Record<string, string> = {
  normative: '目标规范',
  observed: '实际观察',
  documented_intent: '已记录意图',
  design_decision: '设计决定',
  inferred: '推断候选',
  assumption: '待确认假设',
};

const verificationLabels: Record<string, string> = {
  verified: '已核验',
  unverified: '尚未核验',
  contradicted: '存在反证',
  insufficient: '依据不足',
  stale: '需要重新核验',
};

export function knowledgeKindLabel(kind: string): string {
  return kindLabels[kind] ?? '知识条目';
}

export function factKindLabel(kind: string): string {
  return factLabels[kind] ?? '内容性质待核验';
}

export function verificationLabel(status: string): string {
  return verificationLabels[status] ?? '核验状态待确认';
}
