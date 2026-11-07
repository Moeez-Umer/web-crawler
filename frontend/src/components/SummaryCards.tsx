import { useMemo } from "react";
import type { TreeNode } from "../api/types";

interface SummaryCardsProps {
  root: TreeNode;
}

interface TreeStats {
  totalPages: number;
  crawledPages: number;
  averageRatio: number;
  failedPages: number;
}

function computeTreeStats(root: TreeNode): TreeStats {
  let totalPages = 0;
  let crawledPages = 0;
  let ratioSum = 0;
  let failedPages = 0;

  const stack: TreeNode[] = [root];
  while (stack.length > 0) {
    const current = stack.pop();
    if (!current) continue;
    totalPages += 1;
    if (current.status === "Crawled") {
      crawledPages += 1;
      ratioSum += current.domainLinkRatio;
    } else if (current.status === "Failed") {
      failedPages += 1;
    }
    for (const child of current.children) {
      stack.push(child);
    }
  }

  const averageRatio = crawledPages > 0 ? ratioSum / crawledPages : 0;
  return { totalPages, crawledPages, averageRatio, failedPages };
}

export function SummaryCards({ root }: SummaryCardsProps) {
  const stats = useMemo(() => computeTreeStats(root), [root]);
  const avgPercentage =
    stats.crawledPages > 0
      ? `${(stats.averageRatio * 100).toFixed(1)}%`
      : "0.0%";

  return (
    <div className="summary-cards">
      <div className="summary-card">
        <span className="summary-card-label">Total Pages</span>
        <strong className="summary-card-value">{stats.totalPages}</strong>
      </div>
      <div className="summary-card">
        <span className="summary-card-label">
          Avg Domain Link Ratio (Crawled)
        </span>
        <strong className="summary-card-value">{avgPercentage}</strong>
      </div>
      <div className="summary-card">
        <span className="summary-card-label">Failed Pages</span>
        <strong className="summary-card-value">{stats.failedPages}</strong>
      </div>
    </div>
  );
}

