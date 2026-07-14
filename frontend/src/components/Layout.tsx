import { ReactNode } from "react";
import NavBar from "./NavBar";
import AiStatusBanner from "./AiStatusBanner";
import Disclaimer from "./Disclaimer";
import LoadingOverlay from "./LoadingOverlay";

export default function Layout({ children }: { children: ReactNode }) {
  return (
    <div className="app-shell">
      <LoadingOverlay />
      <NavBar />
      <div className="page-container">
        <AiStatusBanner />
        {children}
        <Disclaimer />
      </div>
    </div>
  );
}
