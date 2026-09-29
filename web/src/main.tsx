import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import { AccountsProvider } from './context/AccountsContext'
import { OrderApprovalsProvider } from './context/OrderApprovalsContext'
import './styles.css'

createRoot(document.getElementById('root')!).render(<StrictMode><AccountsProvider><OrderApprovalsProvider><App /></OrderApprovalsProvider></AccountsProvider></StrictMode>)
