# Submission Guide & Reviewer Checklist

## 1. Reviewer Checklist

- [ ] **Clean Build & Containers:** `docker compose up --build -d` starts `postgres`, `rabbitmq`, `api`, and `worker` and all four reach `healthy` status (`docker compose ps`).
- [ ] **Health Endpoints:**
  - `curl -s http://localhost:5000/health` returns `200 OK` (`{"status":"Healthy",...}`).
  - `curl -s http://localhost:5001/health` returns `200 OK` (`Healthy`).
- [ ] **End-to-End Crawl Verification:**
  - `POST /api/jobs` with `{"url":"https://books.toscrape.com","maxDepth":1}` returns `201 Created` with a `jobId`.
  - `GET /api/jobs/{id}` transitions `Pending -> Running -> Completed` with `pendingTasks: 0`.
  - `GET /api/jobs/{id}/tree` returns a hierarchical `TreeNode` with `domainLinkRatio`, `totalLinks`, `internalLinks`, and `children`.
- [ ] **Automated Test Suite:** `dotnet test` passes all 136 tests (unit + PostgreSQL integration tests, 0 failures, 0 skipped when Postgres is running).
- [ ] **Frontend (Optional):** `docker compose --profile frontend up --build -d` (or `cd frontend && npm ci && npm run dev`) serves the React UI at `http://localhost:5173`, builds cleanly (`npm run build`), and passes ESLint (`npm run lint`).
- [ ] **Documentation & CI:** `README.md`, `docs/SPEC.md`, `docs/Decisions.md`, `docs/Flow.md`, `docs/PROGRESS.md`, `docs/api-contract.md`, `docs/how-to-run-tests.md`, `.env.example`, and `.github/workflows/ci.yml` are present and up to date.

---

## 2. Create a Clean Submission ZIP Archive

Run the following command from the repository root (`web-crawler/`) to package the project without build artifacts (`bin/`, `obj/`, `dist/`), `node_modules/`, local `.env` files, or `.git/` metadata:

```bash
zip -r ../web-crawler-submission.zip . \
  -x "*/bin/*" "bin/*" \
  -x "*/obj/*" "obj/*" \
  -x "*/node_modules/*" "node_modules/*" \
  -x "*/dist/*" "dist/*" \
  -x ".git/*" \
  -x ".vs/*" \
  -x ".idea/*" \
  -x "*.DS_Store" \
  -x ".env" ".env.local" ".env.*.local"
```

*(Alternative if all files are committed to Git: `git archive --format=zip --output=../web-crawler-submission.zip HEAD`)*

Verify the archive contents:

```bash
unzip -l ../web-crawler-submission.zip | head -n 40
```

---

## 3. Google Drive Upload & Sharing Steps

1. Open [Google Drive](https://drive.google.com/) and upload `web-crawler-submission.zip`.
2. Right-click `web-crawler-submission.zip` in Google Drive and select **Share** → **Share**.
3. Under **General access**, change **Restricted** to **Anyone with the link** and ensure the role is set to **Viewer**.
4. Click **Copy link** and verify the link opens in an incognito/private browser window without prompting for sign-in.
5. Submit the copied Google Drive URL.

