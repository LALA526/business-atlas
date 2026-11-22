import type { ChangeSet } from '../../shared/api';

export const changeStateLabels: Record<ChangeSet['state'], string> = {
  draft: '草稿', submitted: '等待审核', in_review: '审核中', changes_requested: '待修改',
  approved: '批准待发布', rejected: '已拒绝', published: '已发布', withdrawn: '已撤回', conflict: '基线冲突',
};
