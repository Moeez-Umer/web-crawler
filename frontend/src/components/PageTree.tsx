import {
  useCallback,
  useEffect,
  useId,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type KeyboardEvent,
} from "react";
import type { TreeNode } from "../api/types";

interface PageTreeProps {
  root: TreeNode;
}

function collectExpandableUrls(node: TreeNode, out: Set<string>): void {
  if (node.children.length > 0) {
    out.add(node.url);
    for (const child of node.children) {
      collectExpandableUrls(child, out);
    }
  }
}

function filterTree(node: TreeNode, query: string): TreeNode | null {
  const normalizedQuery = query.toLowerCase();
  const filteredChildren: TreeNode[] = [];
  for (const child of node.children) {
    const filteredChild = filterTree(child, normalizedQuery);
    if (filteredChild) {
      filteredChildren.push(filteredChild);
    }
  }

  const selfMatches = node.url.toLowerCase().includes(normalizedQuery);
  if (selfMatches || filteredChildren.length > 0) {
    return {
      ...node,
      children: filteredChildren,
    };
  }
  return null;
}

interface TreeNodeItemProps {
  node: TreeNode;
  expandedUrls: ReadonlySet<string>;
  forceExpandAll: boolean;
  onToggle: (url: string) => void;
  onTreeItemKeyDown: (
    e: KeyboardEvent<HTMLLIElement>,
    node: TreeNode,
    isExpanded: boolean
  ) => void;
}

function TreeNodeItem({
  node,
  expandedUrls,
  forceExpandAll,
  onToggle,
  onTreeItemKeyDown,
}: TreeNodeItemProps) {
  const hasChildren = node.children.length > 0;
  const isExpanded = hasChildren && (forceExpandAll || expandedUrls.has(node.url));

  const pct = Math.round(node.domainLinkRatio * 100);
  const ratioTierClass =
    pct >= 70 ? "ratio-green" : pct >= 30 ? "ratio-yellow" : "ratio-red";

  return (
    <li
      role="treeitem"
      aria-expanded={hasChildren ? isExpanded : undefined}
      tabIndex={0}
      className="tree-item"
      data-url={node.url}
      onKeyDown={(e) => onTreeItemKeyDown(e, node, isExpanded)}
    >
      <div className="tree-item-row">
        {hasChildren ? (
          <button
            type="button"
            className="tree-chevron-btn"
            aria-expanded={isExpanded}
            aria-label={`${isExpanded ? "Collapse" : "Expand"} ${node.url}`}
            onClick={(e) => {
              e.stopPropagation();
              onToggle(node.url);
            }}
          >
            <span aria-hidden="true">{isExpanded ? "▾" : "▸"}</span>
          </button>
        ) : (
          <span className="tree-chevron-spacer" aria-hidden="true">
            •
          </span>
        )}

        <a
          href={node.url}
          target="_blank"
          rel="noopener noreferrer"
          className="tree-url-link"
          title={node.url}
        >
          {node.url}
        </a>

        <span className="depth-badge">Depth {node.depth}</span>

        <div className="tree-metrics">
          {node.status === "Crawled" && (
            <>
              <div
                className="ratio-widget"
                title={`Domain Link Ratio: ${pct}%`}
              >
                <div className="ratio-bar-track" aria-hidden="true">
                  <div
                    className={`ratio-bar-fill ${ratioTierClass}`}
                    style={{ "--ratio-width": `${pct}%` } as CSSProperties}
                  />
                </div>
                <span className={`ratio-pct ${ratioTierClass}`}>{pct}%</span>
              </div>
              <span className="links-count">
                {node.internalLinks} / {node.totalLinks} links
              </span>
            </>
          )}

          {node.status === "Failed" && (
            <div className="failed-node-info">
              <span className="chip chip-failed">Failed</span>
              {node.error && (
                <span className="tree-error-text" title={node.error}>
                  {node.error}
                </span>
              )}
            </div>
          )}

          {node.status === "Pending" && (
            <span className="chip chip-pending">Pending</span>
          )}
        </div>
      </div>

      {hasChildren && isExpanded && (
        <ul role="group" className="tree-group">
          {node.children.map((child) => (
            <TreeNodeItem
              key={child.url}
              node={child}
              expandedUrls={expandedUrls}
              forceExpandAll={forceExpandAll}
              onToggle={onToggle}
              onTreeItemKeyDown={onTreeItemKeyDown}
            />
          ))}
        </ul>
      )}
    </li>
  );
}

export function PageTree({ root }: PageTreeProps) {
  const filterInputId = useId();
  const treeRef = useRef<HTMLUListElement | null>(null);
  const [filterText, setFilterText] = useState("");
  // Keep tree collapsed beyond depth 1 by default: only root (depth 0) is expanded initially,
  // so root and its depth-1 children are visible while depth >= 2 children remain collapsed.
  const [expandedUrls, setExpandedUrls] = useState<Set<string>>(
    () => new Set([root.url])
  );

  useEffect(() => {
    setExpandedUrls(new Set([root.url]));
  }, [root.url]);

  const handleToggle = useCallback((url: string) => {
    setExpandedUrls((prev) => {
      const next = new Set(prev);
      if (next.has(url)) {
        next.delete(url);
      } else {
        next.add(url);
      }
      return next;
    });
  }, []);

  const handleExpandAll = useCallback(() => {
    const all = new Set<string>();
    collectExpandableUrls(root, all);
    setExpandedUrls(all);
  }, [root]);

  const handleCollapseAll = useCallback(() => {
    setExpandedUrls(new Set());
  }, []);

  const trimmedFilter = filterText.trim();
  const displayedRoot = useMemo(() => {
    if (!trimmedFilter) {
      return root;
    }
    return filterTree(root, trimmedFilter);
  }, [root, trimmedFilter]);

  const handleTreeItemKeyDown = useCallback(
    (
      e: KeyboardEvent<HTMLLIElement>,
      node: TreeNode,
      isExpanded: boolean
    ) => {
      if (e.target !== e.currentTarget) {
        return;
      }

      const treeEl = treeRef.current;
      if (!treeEl) return;

      const visibleItems = Array.from(
        treeEl.querySelectorAll<HTMLLIElement>('[role="treeitem"]')
      );
      const currentIndex = visibleItems.indexOf(e.currentTarget);
      if (currentIndex === -1) return;

      switch (e.key) {
        case "ArrowDown": {
          e.preventDefault();
          e.stopPropagation();
          const next = visibleItems[currentIndex + 1];
          next?.focus();
          break;
        }
        case "ArrowUp": {
          e.preventDefault();
          e.stopPropagation();
          const prev = visibleItems[currentIndex - 1];
          prev?.focus();
          break;
        }
        case "ArrowRight": {
          e.preventDefault();
          e.stopPropagation();
          if (node.children.length > 0) {
            if (!isExpanded) {
              handleToggle(node.url);
            } else {
              const firstChild = e.currentTarget.querySelector<HTMLLIElement>(
                '[role="group"] > [role="treeitem"]'
              );
              firstChild?.focus();
            }
          }
          break;
        }
        case "ArrowLeft": {
          e.preventDefault();
          e.stopPropagation();
          if (node.children.length > 0 && isExpanded) {
            handleToggle(node.url);
          } else {
            const parentGroup = e.currentTarget.parentElement;
            const parentItem =
              parentGroup?.closest<HTMLLIElement>('[role="treeitem"]');
            parentItem?.focus();
          }
          break;
        }
        case "Home": {
          e.preventDefault();
          e.stopPropagation();
          visibleItems[0]?.focus();
          break;
        }
        case "End": {
          e.preventDefault();
          e.stopPropagation();
          visibleItems[visibleItems.length - 1]?.focus();
          break;
        }
        case "Enter":
        case " ": {
          if (node.children.length > 0) {
            e.preventDefault();
            e.stopPropagation();
            handleToggle(node.url);
          }
          break;
        }
      }
    },
    [handleToggle]
  );

  return (
    <div className="page-tree-section">
      <div className="page-tree-toolbar">
        <search className="page-tree-search">
          <label htmlFor={filterInputId} className="page-tree-filter-label">
            Filter URLs
          </label>
          <input
            id={filterInputId}
            type="search"
            className="input page-tree-filter-input"
            placeholder="Filter by URL substring..."
            value={filterText}
            onChange={(e) => setFilterText(e.target.value)}
            autoCorrect="off"
            autoCapitalize="off"
            spellCheck={false}
          />
        </search>

        <div className="page-tree-actions">
          <button
            type="button"
            className="btn btn-secondary btn-sm"
            onClick={handleExpandAll}
          >
            Expand all
          </button>
          <button
            type="button"
            className="btn btn-secondary btn-sm"
            onClick={handleCollapseAll}
          >
            Collapse all
          </button>
        </div>
      </div>

      {displayedRoot ? (
        <ul
          ref={treeRef}
          role="tree"
          aria-label="Crawled pages tree"
          className="tree-root"
        >
          <TreeNodeItem
            node={displayedRoot}
            expandedUrls={expandedUrls}
            forceExpandAll={Boolean(trimmedFilter)}
            onToggle={handleToggle}
            onTreeItemKeyDown={handleTreeItemKeyDown}
          />
        </ul>
      ) : (
        <p className="tree-no-matches">
          No pages match filter &ldquo;{trimmedFilter}&rdquo;.
        </p>
      )}
    </div>
  );
}

