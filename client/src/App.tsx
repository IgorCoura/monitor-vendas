import { Route, Routes } from 'react-router-dom'
import { Layout } from './components/Layout'
import { DashboardPage } from './features/dashboard/DashboardPage'
import { SellerPage } from './features/sellers/SellerPage'
import { RegistryPage } from './features/registry/RegistryPage'
import { HolidaysPage } from './features/holidays/HolidaysPage'
import { LabelsPage } from './features/labels/LabelsPage'
import { ContactsPage } from './features/contacts/ContactsPage'
import { AiAnalysisPage } from './features/ai/AiAnalysisPage'
import { ProxiesPage } from './features/proxies/ProxiesPage'
import { WarmupPage } from './features/warmup/WarmupPage'

export default function App() {
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route path="/" element={<DashboardPage />} />
        <Route path="/sellers/:id" element={<SellerPage />} />
        <Route path="/registry" element={<RegistryPage />} />
        <Route path="/contacts" element={<ContactsPage />} />
        <Route path="/ai" element={<AiAnalysisPage />} />
        <Route path="/labels" element={<LabelsPage />} />
        <Route path="/warmup" element={<WarmupPage />} />
        <Route path="/proxies" element={<ProxiesPage />} />
        <Route path="/holidays" element={<HolidaysPage />} />
      </Route>
    </Routes>
  )
}
