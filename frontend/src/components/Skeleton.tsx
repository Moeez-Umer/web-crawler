interface SkeletonProps {
  rows?: number;
  label?: string;
}

export function Skeleton({ rows = 4, label = "Loading..." }: SkeletonProps) {
  return (
    <div className="skeleton-container" role="status" aria-label={label}>
      {Array.from({ length: rows }).map((_, idx) => (
        <div key={idx} className="skeleton-row" />
      ))}
    </div>
  );
}

