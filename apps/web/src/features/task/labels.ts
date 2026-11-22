export const taskStateLabels: Record<string, string> = {
  captured: '已提出', investigating: '调查中', needs_decision: '需要决策', ready: '可以实施',
  implementing: '实施中', verifying: '验证中', awaiting_knowledge: '待知识审核',
  ready_for_acceptance: '待最终验收', completed: '已完成', blocked: '已阻断', cancelled: '已取消',
};

export const impactStateLabels: Record<string, string> = {
  required: '必须处理', potential: '可能影响', no_change: '确认不变', deferred: '延后处理', unknown: '尚不确定',
  candidate: '候选，尚未实际调查', inspected: '已实际调查', verified: '证据已核验', stale: '依据已过期',
  open: '待处理', resolved: '已处置', deferred_close: '已延后',
};

export const activityStateLabels: Record<string, string> = {
  open: '待分流', addressed: '处理中', closed: '已闭环', withdrawn: '已撤回',
};

export const closureStateLabels: Record<string, string> = {
  blocked: '缺少必要条件', ready_for_acceptance: '可以提交最终验收',
  delivered_with_debt: '已交付但有知识债', complete: '完整验收已完成',
};

export const deliveryStateLabels: Record<string, string> = {
  aligned: '代码、知识与部署一致', pending_review: '代码已记录，知识待审核',
  divergent: '交付版本不一致', rolled_back: '已回滚', evidence_missing: '缺少交付证据',
};

export function shortId(value?: string) {
  return value ? `${value.slice(0, 8)}…` : '未提供';
}
