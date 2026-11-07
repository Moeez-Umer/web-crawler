import { NavLink, Route, Routes } from "react-router-dom";
import { HistoryPage } from "./pages/HistoryPage";
import { JobDetailsPage } from "./pages/JobDetailsPage";
import { StartCrawlPage } from "./pages/StartCrawlPage";

export function App() {
  return (
    <div className="app-shell">
      <header className="app-header">
        <div className="app-header-inner">
          <NavLink to="/" className="brand-link">
            Web Crawler
          </NavLink>
          <nav aria-label="Primary" className="app-nav">
            <NavLink
              to="/"
              end
              className={({ isActive }) =>
                `nav-link ${isActive ? "nav-link-active" : ""}`
              }
            >
              Start Crawl
            </NavLink>
            <NavLink
              to="/history"
              className={({ isActive }) =>
                `nav-link ${isActive ? "nav-link-active" : ""}`
              }
            >
              History
            </NavLink>
          </nav>
        </div>
      </header>

      <main id="main-content" className="app-main">
        <Routes>
          <Route path="/" element={<StartCrawlPage />} />
          <Route path="/jobs/:jobId" element={<JobDetailsPage />} />
          <Route path="/history" element={<HistoryPage />} />
        </Routes>
      </main>
    </div>
  );
}

