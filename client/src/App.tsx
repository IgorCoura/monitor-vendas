import { Route, Routes } from 'react-router-dom'
import { Layout } from './components/Layout'
import { DashboardPage } from './features/dashboard/DashboardPage'
import { SellerPage } from './features/sellers/SellerPage'
import { RegistryPage } from './features/registry/RegistryPage'
import { HolidaysPage } from './features/holidays/HolidaysPage'
import { LabelsPage } from './features/labels/LabelsPage'
import { ContactsPage } from './features/contacts/ContactsPage'

export default function App() {
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route path="/" element={<DashboardPage />} />
        <Route path="/sellers/:id" element={<SellerPage />} />
        <Route path="/registry" element={<RegistryPage />} />
        <Route path="/contacts" element={<ContactsPage />} />
        <Route path="/labels" element={<LabelsPage />} />
        <Route path="/holidays" element={<HolidaysPage />} />
      </Route>
    </Routes>
  )
}
