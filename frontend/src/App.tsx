import { BrowserRouter, Route, Routes } from "react-router-dom";
import { AiStatusProvider } from "./api/AiStatusContext";
import { WorkflowProvider } from "./api/WorkflowContext";
import Layout from "./components/Layout";
import Dashboard from "./pages/Dashboard";
import ProtocolWorkspace from "./pages/ProtocolWorkspace";
import QuestionBankPreview from "./pages/QuestionBankPreview";
import ScreeningSessionPage from "./pages/ScreeningSessionPage";
import SummaryPage from "./pages/SummaryPage";

export default function App() {
  return (
    <BrowserRouter>
      <AiStatusProvider>
        <WorkflowProvider>
          <Layout>
            <Routes>
              <Route path="/" element={<Dashboard />} />
              <Route path="/protocol" element={<ProtocolWorkspace />} />
              <Route path="/questions" element={<QuestionBankPreview />} />
              <Route path="/screening" element={<ScreeningSessionPage />} />
              <Route path="/summary" element={<SummaryPage />} />
            </Routes>
          </Layout>
        </WorkflowProvider>
      </AiStatusProvider>
    </BrowserRouter>
  );
}
