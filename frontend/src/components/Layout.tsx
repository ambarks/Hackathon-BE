import { ReactNode } from "react";
import NavBar from "./NavBar";
import AiStatusBanner from "./AiStatusBanner";
import Disclaimer from "./Disclaimer";

export default function Layout({ children }: { children: ReactNode }) {
  return (
    <div className="app-shell">
      <NavBar />
      <div className="page-container">
        <AiStatusBanner />
        {children}
        <Disclaimer />
      </div>
    </div>
  );
}
