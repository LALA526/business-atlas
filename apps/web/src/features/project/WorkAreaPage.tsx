import { Link, useOutletContext, useParams } from 'react-router-dom';
import type { ProjectOutletContext } from './ProjectShell';

const copy = {
  review: { eyebrow: 'CHANGES & REVIEW', title: '变更与审核', text: '这里将呈现当前账号实际负责的成果、差异与人工审批。批准和发布始终是独立操作。' },
  tasks: { eyebrow: 'TASKS & ISSUES', title: '任务与问题', text: '从真实业务意图提出需求，并查看调查、未知和下一步。平台不会用本地状态冒充真实进展。' },
  delivery: { eyebrow: 'DELIVERY & ACCEPTANCE', title: '交付与验收', text: '代码、测试、知识发布、部署和最终验收分别核对，只有实际证据可以闭合交付。' },
} as const;

export default function WorkAreaPage({ area }: { area: keyof typeof copy }) {
  const { projectId = '' } = useParams();
  useOutletContext<ProjectOutletContext>();
  const content = copy[area];
  return <div className="work-area"><div className="page-heading"><p className="eyebrow">{content.eyebrow}</p><h1>{content.title}</h1><p>{content.text}</p></div><div className="empty-state"><strong>暂无可展示的已授权工作项</strong><p>你仍可从项目概览和知识目录阅读真实已发布内容。</p><Link className="secondary link-button" to={`/p/${projectId}/overview`}>返回项目概览</Link></div></div>;
}
