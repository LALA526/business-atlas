import { useState } from 'react';
import type { FormEvent } from 'react';
import { useMutation } from '@tanstack/react-query';
import { atlasApi, ApiError } from '../../shared/api';
import type { Revision, RuleResult, Schema } from '../../shared/api';
import { UnknownList } from './ReaderContent';

type RuleInput = Schema<'RuleInput'>;
const statusLabels: Record<RuleResult['status'], string> = {
  matched: '符合规则', not_matched: '没有匹配结果', defaulted: '采用默认结果', unknown: '事实不足，无法判断',
  conflict: '规则存在冲突', not_executable: '当前规则不可自动求值',
};
const truthLabels = { true: '成立', false: '不成立', unknown: '未知', conflict: '冲突' } as const;

function inputsOf(revision: Revision): RuleInput[] {
  const inputs = revision.body.inputs;
  return Array.isArray(inputs) ? inputs as RuleInput[] : [];
}

function parseValue(input: RuleInput, raw: string): unknown {
  if (input.valueType === 'boolean') return raw === 'true';
  if (input.valueType === 'integer') return Number.parseInt(raw, 10);
  if (input.valueType === 'decimal') return Number(raw);
  if (input.valueType === 'list') return raw.split(',').map(value => value.trim()).filter(Boolean);
  return raw;
}

export default function CaseEvaluator({ projectId, revision, baselineId }: { projectId: string; revision: Revision; baselineId: string }) {
  const [values, setValues] = useState<Record<string, string>>({});
  const [submitted, setSubmitted] = useState(false);
  const inputs = inputsOf(revision);
  const evaluation = useMutation({ mutationFn: (facts: Record<string, unknown>) => atlasApi.evaluateRule(projectId, revision.unitId, facts, baselineId) });
  const rows = Array.isArray(revision.body.rows) ? revision.body.rows as { rowId?: string; situation?: string }[] : [];
  const submit = (event: FormEvent) => {
    event.preventDefault();
    const facts = Object.fromEntries(inputs.filter(input => values[input.attribute.unitId] !== undefined && values[input.attribute.unitId] !== '').map(input => [input.attribute.unitId, parseValue(input, values[input.attribute.unitId]!)]));
    setSubmitted(true);
    evaluation.mutate(facts);
  };
  if (revision.body.evaluationMode !== 'structured') return <section className="case-evaluator"><h2>用实际业务案例判断</h2><p>这条规则标记为人工判断或尚未形成结构化条件。系统不会猜测结果，请依据规则说明和来源进行人工审核。</p></section>;
  return <section className="case-evaluator" aria-labelledby="case-title">
    <div className="section-heading"><div><p className="eyebrow">CASE EVALUATION</p><h2 id="case-title">用实际业务案例判断</h2></div><span>只计算规则，不执行真实业务动作</span></div>
    <form onSubmit={submit}>
      {inputs.length === 0 && <p className="reader-empty">规则未声明输入字段，无法构造可靠案例。请先在候选修订中补充输入定义。</p>}
      <div className="case-inputs">{inputs.map(input => {
        const id = `fact-${input.attribute.unitId}`;
        const common = { id, value: values[input.attribute.unitId] ?? '', onChange: (event: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) => setValues(current => ({ ...current, [input.attribute.unitId]: event.target.value })) };
        return <label key={input.attribute.unitId} htmlFor={id}><span>{input.label}{input.unit && <small>（{input.unit}）</small>}</span>{input.valueType === 'boolean' ? <select {...common}><option value="">未提供</option><option value="true">是</option><option value="false">否</option></select> : <input {...common} type={input.valueType === 'integer' || input.valueType === 'decimal' ? 'number' : input.valueType === 'date' ? 'date' : input.valueType === 'datetime' ? 'datetime-local' : 'text'} step={input.valueType === 'decimal' ? 'any' : undefined} placeholder={input.valueType === 'list' ? '多个值用逗号分隔' : '留空表示该事实未知'} />}<small>未提供时：{input.missingSemantics}</small></label>;
      })}</div>
      <button className="primary" disabled={!inputs.length || evaluation.isPending}>{evaluation.isPending ? '正在判断…' : submitted ? '重新判断' : '判断这个案例'}</button>
    </form>
    {evaluation.isError && <div className="notice error" role="alert">{evaluation.error instanceof ApiError ? evaluation.error.message : '规则判断失败，请重试。'}</div>}
    {evaluation.data && <div className={`case-result ${evaluation.data.status}`} role="status" aria-live="polite">
      <h3>{statusLabels[evaluation.data.status]}</h3>
      {evaluation.data.outcomes.length > 0 && <div><h4>判断结果</h4>{evaluation.data.outcomes.map((outcome, index) => <p key={index}>{outcome.description}</p>)}</div>}
      {evaluation.data.conditions.length > 0 && <div className="table-scroll"><table><caption>逐项判断依据</caption><thead><tr><th scope="col">条件</th><th scope="col">结果</th><th scope="col">原因</th></tr></thead><tbody>{evaluation.data.conditions.map(condition => <tr key={condition.rowId}><td>{rows.find(row => row.rowId === condition.rowId)?.situation ?? condition.rowId}</td><td>{truthLabels[condition.value]}</td><td>{condition.reason}</td></tr>)}</tbody></table></div>}
      <UnknownList items={evaluation.data.unknowns} />
    </div>}
  </section>;
}
