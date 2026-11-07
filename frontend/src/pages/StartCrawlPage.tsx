import { useId, useState, type FormEvent } from "react";
import { useMutation } from "@tanstack/react-query";
import { useNavigate } from "react-router-dom";
import { ApiError, createJob } from "../api/client";
import type { CreateJobRequest, CreateJobResponse } from "../api/types";
import { ErrorBanner } from "../components/ErrorBanner";

function validateHttpUrl(value: string): string | null {
  const trimmed = value.trim();
  if (!trimmed) {
    return "URL is required.";
  }
  try {
    const parsed = new URL(trimmed);
    if (parsed.protocol !== "http:" && parsed.protocol !== "https:") {
      return "URL must start with http:// or https://.";
    }
    if (!parsed.hostname) {
      return "URL must include a valid hostname.";
    }
    return null;
  } catch {
    return "Enter a valid absolute http:// or https:// URL.";
  }
}

export function StartCrawlPage() {
  const navigate = useNavigate();
  const urlInputId = useId();
  const urlErrorId = useId();
  const depthInputId = useId();
  const depthHintId = useId();
  const depthErrorId = useId();

  const [url, setUrl] = useState("");
  const [maxDepthInput, setMaxDepthInput] = useState("2");
  const [urlError, setUrlError] = useState<string | null>(null);
  const [depthError, setDepthError] = useState<string | null>(null);
  const [apiErrorMessage, setApiErrorMessage] = useState<string | null>(null);

  const mutation = useMutation<CreateJobResponse, ApiError, CreateJobRequest>({
    mutationFn: (payload) => createJob(payload),
    onSuccess: (data) => {
      navigate(`/jobs/${data.jobId}`);
    },
    onError: (err) => {
      const detail =
        err.problemDetails.detail ||
        (err.problemDetails.errors
          ? Object.values(err.problemDetails.errors).flat().join(" ")
          : undefined) ||
        err.problemDetails.title ||
        err.message;
      setApiErrorMessage(detail);
    },
  });

  function validateDepth(value: string): string | null {
    const trimmed = value.trim();
    if (trimmed === "") {
      return null;
    }
    const num = Number(trimmed);
    if (!Number.isInteger(num) || num < 0 || num > 5) {
      return "Max depth must be an integer between 0 and 5.";
    }
    return null;
  }

  function handleSubmit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    setApiErrorMessage(null);

    const nextUrlError = validateHttpUrl(url);
    const nextDepthError = validateDepth(maxDepthInput);
    setUrlError(nextUrlError);
    setDepthError(nextDepthError);

    if (nextUrlError || nextDepthError) {
      return;
    }

    const trimmedDepth = maxDepthInput.trim();
    const payload: CreateJobRequest = {
      url: url.trim(),
      ...(trimmedDepth !== "" ? { maxDepth: Number(trimmedDepth) } : {}),
    };

    mutation.mutate(payload);
  }

  return (
    <section className="page-container start-crawl-page">
      <div className="card">
        <h1 className="page-title">Start a Web Crawl</h1>
        <p className="page-subtitle">
          Enter an absolute <code>http://</code> or <code>https://</code> URL to
          enqueue a background crawl job.
        </p>

        {apiErrorMessage && (
          <ErrorBanner
            title="Could not start crawl"
            message={apiErrorMessage}
            onDismiss={() => setApiErrorMessage(null)}
          />
        )}

        <form className="crawl-form" onSubmit={handleSubmit} noValidate>
          <div className="form-group">
            <label htmlFor={urlInputId} className="form-label">
              Target Website URL <span className="required-mark">*</span>
            </label>
            <input
              id={urlInputId}
              name="url"
              type="url"
              required
              className={`input ${urlError ? "input-invalid" : ""}`}
              placeholder="https://books.toscrape.com"
              value={url}
              onChange={(e) => {
                setUrl(e.target.value);
                if (urlError) {
                  setUrlError(null);
                }
              }}
              onBlur={() => {
                if (url.trim().length > 0) {
                  setUrlError(validateHttpUrl(url));
                }
              }}
              aria-invalid={urlError ? "true" : undefined}
              aria-describedby={urlError ? urlErrorId : undefined}
              autoCorrect="off"
              autoCapitalize="off"
              spellCheck={false}
              disabled={mutation.isPending}
            />
            {urlError && (
              <p id={urlErrorId} className="field-error" role="alert">
                {urlError}
              </p>
            )}
          </div>

          <div className="form-group">
            <label htmlFor={depthInputId} className="form-label">
              Max Depth (optional)
            </label>
            <span id={depthHintId} className="field-hint">
              0 crawls only the starting URL. Allowed range: 0 to 5 (default 2).
            </span>
            <input
              id={depthInputId}
              name="maxDepth"
              type="number"
              min={0}
              max={5}
              step={1}
              className={`input input-narrow ${depthError ? "input-invalid" : ""}`}
              value={maxDepthInput}
              onChange={(e) => {
                setMaxDepthInput(e.target.value);
                if (depthError) {
                  setDepthError(null);
                }
              }}
              onBlur={() => setDepthError(validateDepth(maxDepthInput))}
              aria-invalid={depthError ? "true" : undefined}
              aria-describedby={
                depthError ? `${depthHintId} ${depthErrorId}` : depthHintId
              }
              disabled={mutation.isPending}
            />
            {depthError && (
              <p id={depthErrorId} className="field-error" role="alert">
                {depthError}
              </p>
            )}
          </div>

          <div className="form-actions">
            <button
              type="submit"
              className="btn btn-primary"
              disabled={mutation.isPending}
            >
              {mutation.isPending && (
                <span className="spinner" aria-hidden="true" />
              )}
              <span>
                {mutation.isPending ? "Submitting..." : "Start Crawl"}
              </span>
            </button>
          </div>
        </form>
      </div>
    </section>
  );
}

