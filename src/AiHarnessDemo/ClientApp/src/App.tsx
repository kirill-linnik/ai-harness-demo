import { HashRouter, Navigate, Outlet, Route, Routes } from "react-router-dom";
import { useBootstrapQuery } from "./api/queries";
import { BootScreen } from "./components/BootScreen";
import { FatalScreen } from "./components/FatalScreen";
import { useRegisterGlobalToast } from "./lib/toast";
import { FactoryOverviewPage } from "./pages/factory/FactoryOverviewPage";
import { FlowPage } from "./pages/flow/FlowPage";
import { IntakePage } from "./pages/intake/IntakePage";
import { SettingsPage } from "./pages/settings/SettingsPage";
import { HistoryPage } from "./pages/history/HistoryPage";
import { MemoryPage } from "./pages/memory/MemoryPage";
import { PreviewPage } from "./pages/preview/PreviewPage";

/**
 * Layout route: fetches the bootstrap payload once (shared React Query cache with every page
 * below it) and gates rendering exactly like the original `initialize()`/`renderRoute()` did —
 * a boot screen while the very first request is in flight, a fatal screen if it fails. The
 * `/preview/:id` route is intentionally outside this gate: the original `renderPreview()` never
 * depended on `state.bootstrap` either.
 */
function BootstrapGate() {
  const bootstrapQuery = useBootstrapQuery();

  if (bootstrapQuery.isLoading) return <BootScreen />;
  if (bootstrapQuery.isError) {
    const error = bootstrapQuery.error;
    return <FatalScreen message={error instanceof Error ? error.message : String(error)} />;
  }

  return <Outlet />;
}

export function App() {
  useRegisterGlobalToast();

  return (
    <HashRouter>
      <Routes>
        <Route path="/preview/:id" element={<PreviewPage />} />
        <Route element={<BootstrapGate />}>
          <Route path="/factory" element={<FactoryOverviewPage />} />
          <Route path="/factory/:id" element={<FlowPage />} />
          <Route path="/intake" element={<IntakePage />} />
          <Route path="/intake/:id" element={<IntakePage />} />
          <Route path="/settings" element={<SettingsPage />} />
          <Route path="/history" element={<HistoryPage />} />
          <Route path="/memory" element={<MemoryPage />} />
          <Route path="*" element={<Navigate to="/factory" replace />} />
        </Route>
      </Routes>
    </HashRouter>
  );
}
