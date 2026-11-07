interface ErrorBannerProps {
  title?: string;
  message: string;
  onDismiss?: () => void;
  onRetry?: () => void;
}

export function ErrorBanner({
  title = "Error",
  message,
  onDismiss,
  onRetry,
}: ErrorBannerProps) {
  return (
    <div className="error-banner" role="alert">
      <div className="error-banner-content">
        <strong className="error-banner-title">{title}:</strong>{" "}
        <span className="error-banner-message">{message}</span>
      </div>
      {(onRetry || onDismiss) && (
        <div className="error-banner-actions">
          {onRetry && (
            <button
              type="button"
              className="btn btn-secondary btn-sm"
              onClick={onRetry}
            >
              Retry
            </button>
          )}
          {onDismiss && (
            <button
              type="button"
              className="btn btn-ghost btn-sm"
              onClick={onDismiss}
              aria-label="Dismiss error"
            >
              Dismiss
            </button>
          )}
        </div>
      )}
    </div>
  );
}

